using ParadiERP.Application.Contracts;
using ParadiERP.Application.Files;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Rentals;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Customers;

/// <summary>
/// The copies of a customer's identity document (owner, 2026-10-06): attached when the customer
/// is added, optional, and added later from the edit when they were not. The file goes to the
/// files folder and the row keeps its path (owner, 2026-10-04); it leaves only through the
/// customer's own route, under <c>customers.view</c>.
///
/// <para>
/// The same four types and the same 5 MB as the copy kept with a contract
/// (<see cref="RentalAttachments"/>), read from the file's own first bytes, never from what the
/// browser claims. A soft-deleted customer is gone here too: nothing is added to, listed for or
/// read from one.
/// </para>
/// </summary>
public sealed class CustomerAttachments(PosDbContext db, IFileStore files)
{
    public const string IdDocument = "idDocument";

    /// <summary>The two sides of a card, a licence and a passport, with room to spare.</summary>
    public const int MaxPerCustomer = 10;

    public const string TooLargeMessage =
        "The file is larger than 5 MB. Take the photo again at a lower resolution, or scan it as a smaller PDF.";

    /// <summary>
    /// Keeps a copy with the customer. The file is written before the row: a row that failed to
    /// save leaves a file nothing points at, which costs disk space; the other order could leave a
    /// row pointing at nothing, which costs the paper.
    /// </summary>
    public async Task<RentalResult<CustomerAttachmentDto>> AddAsync(
        Guid customerId, string? fileName, byte[] content, string? by, CancellationToken ct = default)
    {
        if (!await ExistsAsync(customerId, ct))
            return RentalResult<CustomerAttachmentDto>.NotFound("Customer not found.");
        if (content.Length == 0)
            return RentalResult<CustomerAttachmentDto>.Invalid("The file is empty.");
        if (content.Length > RentalDocuments.MaxAttachmentBytes)
            return RentalResult<CustomerAttachmentDto>.Invalid(TooLargeMessage);
        if (RentalDocuments.SniffContentType(content) is not string contentType)
            return RentalResult<CustomerAttachmentDto>.Invalid("Only a JPEG, PNG or WebP image, or a PDF, can be kept with a customer.");
        if (await db.CustomerAttachments.CountAsync(a => a.CustomerId == customerId, ct) >= MaxPerCustomer)
            return RentalResult<CustomerAttachmentDto>.Conflict($"A customer keeps at most {MaxPerCustomer} files. Remove one first.");

        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/')).Trim();
        if (name.Length == 0) name = IdDocument;
        if (name.Length > 200) name = name[^200..];

        var attachment = new CustomerAttachment
        {
            CustomerId = customerId,
            Kind = IdDocument,
            FileName = name,
            ContentType = contentType,
            Length = content.Length,
            UploadedBy = by is null ? null : by.Length > 100 ? by[..100] : by
        };
        attachment.StoragePath = FilePaths.CustomerAttachment(customerId, attachment.Id, contentType);
        await files.WriteAsync(attachment.StoragePath, content, ct);

        db.CustomerAttachments.Add(attachment);
        await db.SaveChangesAsync(ct);
        return RentalResult<CustomerAttachmentDto>.Success(ToDto(attachment));
    }

    /// <summary>Every copy kept with the customer, oldest first; never the bytes.</summary>
    public async Task<RentalResult<List<CustomerAttachmentDto>>> ListAsync(Guid customerId, CancellationToken ct = default)
    {
        if (!await ExistsAsync(customerId, ct))
            return RentalResult<List<CustomerAttachmentDto>>.NotFound("Customer not found.");
        var rows = await db.CustomerAttachments.AsNoTracking().Where(a => a.CustomerId == customerId).ToListAsync(ct);
        return RentalResult<List<CustomerAttachmentDto>>.Success(rows.OrderBy(a => a.UploadedAtUtc).Select(ToDto).ToList());
    }

    /// <summary>The row and its file for reading, or null when either is missing. The caller disposes the stream.</summary>
    public async Task<(CustomerAttachment Attachment, Stream Content)?> OpenAsync(
        Guid customerId, Guid attachmentId, CancellationToken ct = default)
    {
        if (!await ExistsAsync(customerId, ct)) return null;
        var attachment = await db.CustomerAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId && a.CustomerId == customerId, ct);
        if (attachment is null) return null;
        var content = await files.OpenReadAsync(attachment.StoragePath, ct);
        return content is null ? null : (attachment, content);
    }

    /// <summary>
    /// Takes a copy off the customer — a blurred photo, the wrong card. Nothing signed points at
    /// it (a contract keeps its own copy), so it may go at any time. The file goes once the row is
    /// gone, never before.
    /// </summary>
    public async Task<RentalResult<bool>> RemoveAsync(Guid customerId, Guid attachmentId, CancellationToken ct = default)
    {
        if (!await ExistsAsync(customerId, ct)) return RentalResult<bool>.NotFound("Customer not found.");
        var attachment = await db.CustomerAttachments.FirstOrDefaultAsync(a => a.Id == attachmentId && a.CustomerId == customerId, ct);
        if (attachment is null) return RentalResult<bool>.NotFound("Attachment not found.");
        db.CustomerAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        await files.DeleteAsync(attachment.StoragePath, ct);
        return RentalResult<bool>.Success(true);
    }

    private Task<bool> ExistsAsync(Guid customerId, CancellationToken ct) =>
        db.Customers.AnyAsync(c => c.Id == customerId && !c.IsDeleted, ct);

    private static CustomerAttachmentDto ToDto(CustomerAttachment a) =>
        new(a.Id, a.Kind, a.FileName, a.ContentType, a.Length, a.UploadedAtUtc, a.UploadedBy);
}
