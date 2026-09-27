using Coglatas.Application.StudentRecords;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Web.Controllers;

[ApiController]
[Authorize]
public sealed class StudentRecordsController(IStudentRecordService studentRecords) : ApiResultControllerBase
{
    [HttpGet("api/student-records/{studentRecordId:guid}/public")]
    public async Task<IActionResult> GetPublic(Guid studentRecordId, CancellationToken cancellationToken)
    {
        return ToActionResult(await studentRecords.GetPublicAsync(studentRecordId, cancellationToken));
    }

    [HttpGet("api/student-records/{studentRecordId:guid}/restricted")]
    public async Task<IActionResult> GetRestricted(
        Guid studentRecordId,
        [FromQuery] string[] fields,
        [FromQuery] bool includePublic,
        CancellationToken cancellationToken)
    {
        return ToActionResult(await studentRecords.GetRestrictedAsync(
            studentRecordId,
            new StudentRecordRestrictedRequest(fields, includePublic),
            cancellationToken));
    }

    [HttpPost("api/student-records/{studentRecordId:guid}/restricted/export-requests")]
    public async Task<IActionResult> RequestRestrictedExport(
        Guid studentRecordId,
        [FromBody] StudentRecordExportRequest request,
        CancellationToken cancellationToken)
    {
        return ToActionResult(await studentRecords.RequestRestrictedExportAsync(studentRecordId, request, cancellationToken));
    }

    [HttpPost("api/student-records/restricted/export-packages/{exportPackageGrantId:guid}/build")]
    public async Task<IActionResult> BuildRestrictedExport(Guid exportPackageGrantId, CancellationToken cancellationToken)
    {
        return ToActionResult(await studentRecords.BuildRestrictedExportAsync(exportPackageGrantId, cancellationToken));
    }

    [HttpGet("api/student-records/restricted/export-packages/{exportPackageGrantId:guid}/download")]
    public async Task<IActionResult> DownloadRestrictedExport(Guid exportPackageGrantId, CancellationToken cancellationToken)
    {
        return ToActionResult(await studentRecords.DownloadRestrictedExportAsync(exportPackageGrantId, cancellationToken));
    }

}
