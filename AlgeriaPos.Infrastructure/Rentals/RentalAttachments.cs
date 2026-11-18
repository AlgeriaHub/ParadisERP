using AlgeriaPos.Application.Files;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Rentals;

/// <summary>
/// The files kept with a contract (answer 10a: a copy of the signatory's identity document). The
/// file goes to the files folder and the row keeps its path (owner, 2026-10-04); it leaves only
/// through the contract's own endpoint, under <c>rentals.view</c>.
/// </summary>
public sealed class RentalAttachments(PosDbContext db, IFileStore files)
{
    /// <summary>The signatory's identity document, both sides, with room to spare: the customer's own cap.</summary>
    public const int MaxPerContract = 10;

    /// <summary>
    /// Keeps a file with a contract — the scan or photo of the signatory's identity document.
    ///
    /// <para>
    /// The type is read from the file's own first bytes, not taken from what the browser claims,
    /// and only four are kept: JPEG, PNG, WebP and PDF. Allowed at any time, because the copy of
    /// the paper may reach the office after the goods left; removing one is allowed only before
    /// the lock (<see cref="RemoveAsync"/>).
    /// </para>
    /// <para>
    /// The file is written before the row. A row that failed to save leaves a file nothing points
    /// at, which costs disk space; the other order could leave a row pointing at nothing, which
    /// costs the paper.
    /// </para>
    /// </summary>
    public async Task<RentalResult<RentalContractAttachment>> AddAsync(
        Guid contractId, string? kind, string? fileName, byte[] content, string? by, CancellationToken ct = default)
    {
        if (!await db.RentalContracts.AnyAsync(c => c.Id == contractId, ct))
            return RentalResult<RentalContractAttachment>.NotFound("Contract not found.");
        if (!string.Equals(kind?.Trim(), RentalDocuments.IdDocumentAttachment, StringComparison.Ordinal))
            return RentalResult<RentalContractAttachment>.Invalid("Only the signatory's identity document (kind idDocument) can be kept with a contract.");
        if (content.Length == 0)
            return RentalResult<RentalContractAttachment>.Invalid("The file is empty.");
        if (content.Length > RentalDocuments.MaxAttachmentBytes)
            return RentalResult<RentalContractAttachment>.Invalid("The file is larger than 5 MB. Take the photo again at a lower resolution, or scan it as a smaller PDF.");
        if (RentalDocuments.SniffContentType(content) is not string contentType)
            return RentalResult<RentalContractAttachment>.Invalid("Only a JPEG, PNG or WebP image, or a PDF, can be kept with a contract.");
        // A cap per contract, as a customer has (security review, 2026-10-10): with none, one
        // contract could take files until the disk was full.
        if (await db.RentalContractAttachments.CountAsync(x => x.ContractId == contractId, ct) >= MaxPerContract)
            return RentalResult<RentalContractAttachment>.Conflict($"A contract keeps at most {MaxPerContract} files. Remove one first.");

        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/')).Trim();
        if (name.Length == 0) name = RentalDocuments.IdDocumentAttachment;
        if (name.Length > 200) name = name[^200..];

        var attachment = new RentalContractAttachment
        {
            ContractId = contractId,
            Kind = RentalDocuments.IdDocumentAttachment,
            FileName = name,
            ContentType = contentType,
            Length = content.Length,
            UploadedBy = by is null ? null : by.Length > 100 ? by[..100] : by
        };
        attachment.StoragePath = FilePaths.ContractAttachment(contractId, attachment.Id, contentType);
        await files.WriteAsync(attachment.StoragePath, content, ct);

        db.RentalContractAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        return RentalResult<RentalContractAttachment>.Success(attachment);
    }

    /// <summary>The row and its file for reading, or null when either is missing. The caller disposes the stream.</summary>
    public async Task<(RentalContractAttachment Attachment, Stream Content)?> OpenAsync(
        Guid contractId, Guid attachmentId, CancellationToken ct = default)
    {
        var attachment = await db.RentalContractAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.ContractId == contractId, ct);
        if (attachment is null) return null;
        var content = await files.OpenReadAsync(attachment.StoragePath, ct);
        return content is null ? null : (attachment, content);
    }

    /// <summary>
    /// Takes a file off a contract — before its first issue note only. After the lock the scan is
    /// part of a signed paper that has been acted on, and like every figure on it, it stays. The
    /// file goes once the row is gone, never before.
    /// </summary>
    public async Task<RentalResult<bool>> RemoveAsync(Guid contractId, Guid attachmentId, CancellationToken ct = default)
    {
        var contract = await db.RentalContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct);
        if (contract is null) return RentalResult<bool>.NotFound("Contract not found.");
        var attachment = await db.RentalContractAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId && a.ContractId == contractId, ct);
        if (attachment is null) return RentalResult<bool>.NotFound("Attachment not found.");
        if (contract.LockedAtUtc is not null)
            return RentalResult<bool>.Conflict("This contract is locked: the identity document kept with it can no longer be removed.");
        db.RentalContractAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        await files.DeleteAsync(attachment.StoragePath, ct);
        return RentalResult<bool>.Success(true);
    }
}
