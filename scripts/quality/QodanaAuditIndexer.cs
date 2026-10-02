// Read-only evidence indexer. This never builds or executes the application.
// Binding uses one synthetic compilation; unresolved external dependencies are
// explicitly reported and must not be interpreted as proof of non-use.
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length != 2) throw new ArgumentException("Usage: SOURCE_ROOT EVIDENCE_DIRECTORY");
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
var files = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains("/Migrations/") && !p.Contains("/obj/") && !p.Contains("/bin/"))
    .OrderBy(p => p, StringComparer.Ordinal).ToArray();
var trees = files.Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), parseOptions,
    Path.GetRelativePath(source, p).Replace('\\', '/'))).ToArray();
var implicitUsings = CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;", parseOptions, "audit-implicit-usings.cs");
var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
var references = tpa.Where(File.Exists).Distinct().Select(p => MetadataReference.CreateFromFile(p));
var compilation = CSharpCompilation.Create("ReadOnlyAudit", trees.Append(implicitUsings), references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
var models = trees.ToDictionary(t => t.FilePath, t => compilation.GetSemanticModel(t));
var roots = trees.ToDictionary(t => t.FilePath, t => t.GetRoot());
var types = new List<object>();
var methods = new List<object>();
var calls = new List<object>();
var uses = new List<object>();
var findings = new List<object>();
var wanted = new HashSet<string>(StringComparer.Ordinal);
var sinkNames = new HashSet<string>(new[] { "Ok", "Created", "CreatedAtAction", "Accepted", "AcceptedAtAction", "StatusCode", "Json", "Serialize", "SerializeToUtf8Bytes", "SerializeAsync", "SerializeAsyncEnumerable", "WriteAsJsonAsync", "SendAsync", "SendCoreAsync", "ToActionResult", "FromResult", "Success", "Fail", "Failure", "MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete" });

string Display(ISymbol? symbol) => symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? "";
int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
int EndLine(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
string Clip(string text, int size = 1800) => text.Length <= size ? text : text[..size] + " [truncated]";
string Attributes(ISymbol? symbol) => symbol == null ? "" : string.Join("; ", symbol.GetAttributes().Select(a => a.ToString()));
string Location(ISymbol symbol) => string.Join(";", symbol.Locations.Where(l => l.IsInSource).Select(l => l.SourceTree!.FilePath + ":" + (l.GetLineSpan().StartLinePosition.Line + 1)));
string Enclosing(SyntaxNode node, SemanticModel model) => Display(model.GetEnclosingSymbol(node.SpanStart));
ISymbol? Declaration(SyntaxNode node, SemanticModel model)
{
    foreach (var parent in node.AncestorsAndSelf())
    {
        ISymbol? symbol = parent switch
        {
            ParameterSyntax p => model.GetDeclaredSymbol(p),
            PropertyDeclarationSyntax p => model.GetDeclaredSymbol(p),
            VariableDeclaratorSyntax v => model.GetDeclaredSymbol(v),
            MethodDeclarationSyntax m => model.GetDeclaredSymbol(m),
            ConstructorDeclarationSyntax c => model.GetDeclaredSymbol(c),
            EnumMemberDeclarationSyntax e => model.GetDeclaredSymbol(e),
            BaseTypeDeclarationSyntax t => model.GetDeclaredSymbol(t),
            LocalFunctionStatementSyntax l => model.GetDeclaredSymbol(l),
            _ => null
        };
        if (parent is ParameterSyntax parameter && parameter.Parent?.Parent is RecordDeclarationSyntax record)
        {
            symbol = model.GetDeclaredSymbol(record)?.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>().FirstOrDefault() ?? symbol;
        }
        if (symbol != null) return symbol;
    }
    return null;
}

foreach (var state in new[] { "open", "dismissed" })
{
    using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "alerts-" + state + ".json")));
    foreach (var alert in document.RootElement.EnumerateArray())
    {
        if (alert.GetProperty("tool").GetProperty("name").GetString() != "QDNETC") continue;
        var instance = alert.GetProperty("most_recent_instance");
        var location = instance.GetProperty("location");
        string path = location.GetProperty("path").GetString()!;
        if (!models.TryGetValue(path, out var model)) continue;
        var root = roots[path];
        var text = root.SyntaxTree.GetText();
        int row = location.GetProperty("start_line").GetInt32() - 1;
        int column = location.TryGetProperty("start_column", out var col) ? col.GetInt32() - 1 : 0;
        if (row < 0 || row >= text.Lines.Count) throw new InvalidDataException("Invalid alert line");
        int position = text.Lines[row].Start + Math.Min(Math.Max(0, column), text.Lines[row].Span.Length);
        var node = root.FindToken(position).Parent!;
        var declaration = Declaration(node, model);
        if (declaration != null) wanted.Add(Display(declaration.OriginalDefinition));
        var context = node.AncestorsAndSelf().FirstOrDefault(n => n is StatementSyntax or PropertyDeclarationSyntax or ParameterSyntax or EnumMemberDeclarationSyntax) ?? node;
        findings.Add(new { number = alert.GetProperty("number").GetInt32(), path, line = row + 1,
            symbol = Display(declaration), symbol_kind = declaration?.Kind.ToString(), containing_type = Display(declaration?.ContainingType),
            symbol_location = declaration == null ? "" : Location(declaration), attributes = Attributes(declaration),
            node_kind = node.Kind().ToString(), context = Clip(context.ToString()), context_line = Line(context), context_end_line = EndLine(context),
            exact_line = text.Lines[row].ToString() });
    }
}

foreach (var tree in trees)
{
    var model = models[tree.FilePath];
    var root = roots[tree.FilePath];
    foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
    {
        if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol) continue;
        types.Add(new { type = Display(symbol), path = tree.FilePath, line = Line(declaration), end_line = EndLine(declaration),
            kind = symbol.TypeKind.ToString(), accessibility = symbol.DeclaredAccessibility.ToString(), attributes = Attributes(symbol),
            base_type = Display(symbol.BaseType), interfaces = symbol.Interfaces.Select(Display).ToArray(),
            properties = symbol.GetMembers().OfType<IPropertySymbol>().Select(p => new { name = p.Name, type = Display(p.Type),
                symbol = Display(p), attributes = Attributes(p), accessibility = p.DeclaredAccessibility.ToString(),
                getter_accessibility = p.GetMethod?.DeclaredAccessibility.ToString(), setter_accessibility = p.SetMethod?.DeclaredAccessibility.ToString(),
                init_only = p.SetMethod?.IsInitOnly, is_static = p.IsStatic, location = Location(p) }).ToArray() });
    }
    foreach (var declaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
    {
        var symbol = model.GetDeclaredSymbol(declaration);
        methods.Add(new { symbol = Display(symbol), containing_type = Display(symbol?.ContainingType), name = declaration.Identifier.ValueText,
            path = tree.FilePath, line = Line(declaration), end_line = EndLine(declaration),
            return_type = Display(symbol?.ReturnType), attributes = Attributes(symbol), accessibility = symbol?.DeclaredAccessibility.ToString(),
            parameters = symbol?.Parameters.Select(p => new { name = p.Name, type = Display(p.Type), attributes = Attributes(p) }).ToArray() });
    }
    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        var info = model.GetSymbolInfo(invocation);
        var symbol = info.Symbol as IMethodSymbol;
        var name = invocation.Expression switch { MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText, SimpleNameSyntax s => s.Identifier.ValueText, _ => "" };
        if (!sinkNames.Contains(name) && symbol?.ContainingAssembly?.Name != "ReadOnlyAudit") continue;
        calls.Add(new { name, symbol = Display(symbol), candidate_reason = info.CandidateReason.ToString(),
            path = tree.FilePath, line = Line(invocation), end_line = EndLine(invocation), enclosing = Enclosing(invocation, model),
            expression = Clip(invocation.ToString()), return_type = Display(model.GetTypeInfo(invocation).Type),
            arguments = invocation.ArgumentList.Arguments.Select(a => new { expression = Clip(a.Expression.ToString(), 600),
                type = Display(model.GetTypeInfo(a.Expression).Type), converted_type = Display(model.GetTypeInfo(a.Expression).ConvertedType) }).ToArray() });
    }
    foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
    {
        var symbol = model.GetSymbolInfo(identifier).Symbol;
        if (symbol == null || !wanted.Contains(Display(symbol.OriginalDefinition))) continue;
        uses.Add(new { symbol = Display(symbol.OriginalDefinition), path = tree.FilePath, line = Line(identifier),
            enclosing = Enclosing(identifier, model), expression = Clip(identifier.Parent?.ToString() ?? identifier.ToString(), 600),
            parent_kind = identifier.Parent?.Kind().ToString() });
    }
}
var result = new { mode = "READ_ONLY_SYNTHETIC_ROSLYN_INDEX", warning = "This is evidence indexing, not an application build or whole-program proof. Missing NuGet references can leave calls unresolved. Sinks require source review before decisions.",
    source_file_count = trees.Length, roslyn_version = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(),
    syntax_error_count = trees.Sum(t => t.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error)),
    target_declarations = findings, types, methods, calls, symbol_uses = uses };
Directory.CreateDirectory(output);
File.WriteAllText(Path.Combine(output, "source-index.json"), JsonSerializer.Serialize(result));
Console.WriteLine($"Indexed {trees.Length} source files, {findings.Count} alerts, {types.Count} types, {methods.Count} methods, {calls.Count} calls and {uses.Count} relevant references.");
