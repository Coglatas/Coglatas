using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

try
{
    if (args.Length != 2) throw new InvalidOperationException("Expected source root and Release symbols.");
    var root = Path.GetFullPath(args[0]);
    var options = new CSharpParseOptions(LanguageVersion.CSharp14, preprocessorSymbols: args[1].Split(';', StringSplitOptions.RemoveEmptyEntries));
    CompilationUnitSyntax Read(string path)
    {
        var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path)), options, path);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidOperationException(string.Join("; ", errors.Select(d => d.ToString())));
        return tree.GetCompilationUnitRoot();
    }
    string Canonical(SyntaxNode node, string text)
    {
        text = text.Replace("global::", "");
        var aliases = node.AncestorsAndSelf().OfType<CompilationUnitSyntax>().Single().Usings
            .Where(u => u.Alias != null).ToDictionary(u => u.Alias!.Name.Identifier.ValueText, u => u.Name!.ToString().Replace("global::", ""));
        var first = text.Split('.', ':')[0];
        return aliases.TryGetValue(first, out var value) ? value + text[first.Length..].Replace("::", ".") : text;
    }
    string Name(SyntaxNode node) => Canonical(node, node.ToString()).Split('.').Last().Replace("Attribute", "");
    string TypeName(TypeSyntax type) => Canonical(type, type.WithoutTrivia().ToString()).Replace("System.Threading.Tasks.", "").Replace("System.", "").Replace("Coglatas.Web.Realtime.", "").Replace(" ", "");
    string? Literal(ExpressionSyntax? expression) => expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;
    ClassDeclarationSyntax Class(CompilationUnitSyntax source, string name)
    {
        var matches = source.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.ValueText == name && c.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException($"Expected one non-nested {name} class");
        return matches[0];
    }
    object[] Attributes(SyntaxList<AttributeListSyntax> lists) => lists.SelectMany(l => l.Attributes).Select(a => (object)new { name = Name(a.Name), arguments = a.ArgumentList?.Arguments.Select(x => x.ToString()).ToArray() }).ToArray();
    string CallName(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText : "";
    var program = Read("src/Coglatas.Web/Program.cs");
    var hub = Class(Read("src/Coglatas.Web/Realtime/AppHub.cs"), "AppHub");
    var calls = program.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
    var mappings = calls.Where(c => CallName(c) == "MapHub" && c.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
        && generic.TypeArgumentList.Arguments.Any(t => TypeName(t) == "AppHub"));
    var mapData = mappings.Select(m => new {
        direct = m.Parent is ExpressionStatementSyntax { Parent: GlobalStatementSyntax },
        receiver = ((MemberAccessExpressionSyntax)m.Expression).Expression.ToString(),
        route = m.ArgumentList.Arguments.Count == 1 ? Literal(m.ArgumentList.Arguments[0].Expression) : null,
        earlyExit = program.Members.OfType<GlobalStatementSyntax>().Where(s => s.SpanStart < m.SpanStart)
            .Any(s => s.Statement is ReturnStatementSyntax or ThrowStatementSyntax
                || s.Statement is ExpressionStatementSyntax e && e.Expression is InvocationExpressionSyntax invocation && CallName(invocation) is "Run" or "RunAsync")
    }).ToArray();
    var authentication = calls.Where(c => CallName(c) == "AddAuthentication").Select(c => c.ArgumentList.Arguments.Count == 1 ? Canonical(c, c.ArgumentList.Arguments[0].Expression.ToString()) : "unsupported").ToArray();
    var methods = hub.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword)).Select(m => new {
        name = m.Identifier.ValueText, returnType = TypeName(m.ReturnType),
        parameterTypes = m.ParameterList.Parameters.Select(p => (p.Modifiers.Any(SyntaxKind.RefKeyword) ? "ref " : p.Modifiers.Any(SyntaxKind.OutKeyword) ? "out " : p.Modifiers.Any(SyntaxKind.InKeyword) ? "in " : "") + (p.Type == null ? "missing" : TypeName(p.Type))).ToArray(),
        isStatic = m.Modifiers.Any(SyntaxKind.StaticKeyword), generic = m.TypeParameterList != null, attributes = Attributes(m.AttributeLists)
    }).ToArray();
    var events = new HashSet<string>();
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src/Coglatas.Web/Realtime"), "*.cs"))
    foreach (var call in Read(Path.GetRelativePath(root, file)).DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => CallName(c) == "SendAsync"))
        if (Literal(call.ArgumentList.Arguments.FirstOrDefault()?.Expression) is { } name) events.Add(name);
    var controller = Class(Read("src/Coglatas.Web/Controllers/SecurityController.cs"), "SecurityController");
    var csrf = controller.Members.OfType<MethodDeclarationSyntax>().SingleOrDefault(m => m.Identifier.ValueText == "CsrfToken");
    string? Route(SyntaxList<AttributeListSyntax> lists, string name) => lists.SelectMany(l => l.Attributes).Where(a => Name(a.Name) == name).Select(a => Literal(a.ArgumentList?.Arguments.FirstOrDefault()?.Expression)).SingleOrDefault();
    var security = Class(Read("src/Coglatas.Web/Configuration/SecurityOptions.cs"), "SecurityOptions");
    var header = security.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables).SingleOrDefault(v => v.Identifier.ValueText == "CsrfHeaderName");
    var response = csrf?.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(c => TypeName(c.Type) == "CsrfTokenResponse").ToArray() ?? [];
    Console.WriteLine(JsonSerializer.Serialize(new {
        mappings = mapData, authentication,
        hub = new { attributes = Attributes(hub.AttributeLists), isPublic = hub.Modifiers.Any(SyntaxKind.PublicKeyword), isSealed = hub.Modifiers.Any(SyntaxKind.SealedKeyword), methods },
        events = events.Order().ToArray(),
        csrf = new { route = Route(controller.AttributeLists, "Route"), action = csrf == null ? null : Route(csrf.AttributeLists, "HttpGet"),
            isPublic = csrf?.Modifiers.Any(SyntaxKind.PublicKeyword) == true,
            anonymous = csrf?.AttributeLists.SelectMany(l => l.Attributes).Any(a => Name(a.Name) == "AllowAnonymous") == true,
            returnType = csrf == null ? null : TypeName(csrf.ReturnType), header = Literal(header?.Initializer?.Value),
            responseHeader = response.Length == 1 && response[0].ArgumentList?.Arguments.Count == 2 ? Canonical(response[0], response[0].ArgumentList!.Arguments[1].Expression.ToString()) : null }
    }));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Roslyn source inspection failed: {error.Message}");
    return 1;
}
