using DataHub.Application;
using DataHub.Domain;

namespace DataHub.Web.Portal;

public sealed record Bilingual(string Ka, string En);

/// <summary>Customer-facing texts for error codes and statuses (Georgian first, English second).</summary>
public static class PortalText
{
    private static readonly Dictionary<string, Bilingual> Errors = new()
    {
        ["LINK_INVALID"] = new("ბმული არასწორია ან ვადა გაუვიდა. მიმართეთ ბანკს ახალი ბმულისთვის.", "The link is invalid or has expired. Please ask the bank for a new link."),
        ["DETAILS_MISMATCH"] = new("შეყვანილი მონაცემები არ ემთხვევა ამ ბმულს.", "The details you entered do not match this link."),
        ["OTP_RATE_LIMITED"] = new("გთხოვთ, მოიცადოთ ახალი კოდის მოთხოვნამდე.", "Please wait before requesting a new code."),
        ["OTP_WRONG"] = new("კოდი არასწორია.", "The code is incorrect."),
        ["OTP_EXPIRED"] = new("კოდს ვადა გაუვიდა. მოითხოვეთ ახალი კოდი.", "The code has expired. Please request a new one."),
        ["UPLOAD_TYPE_DISABLED"] = new("ORIS-ის გატარებების ფაილის ატვირთვა ჯერ ხელმისაწვდომი არ არის.", "Uploading the ORIS entries sheet is not available yet."),
        ["UPLOAD_WRONG_EXTENSION"] = new("აირჩიეთ სწორი ტიპის ფაილი.", "Please select a file of the correct type."),
        ["UPLOAD_TOO_LARGE"] = new("ფაილი ძალიან დიდია (მაქს. 2 GB).", "The file is too large (max 2 GB)."),
        ["UPLOAD_PROCESSING"] = new("წინა ფაილი ჯერ კიდევ მუშავდება.", "A previous upload is still being processed."),
        ["UPLOAD_NOT_ZIP"] = new("ფაილი არ არის ZIP არქივი.", "The file is not a valid ZIP archive."),
        ["UPLOAD_NO_TPS"] = new("არქივში ORIS-ის .tps ფაილები ვერ მოიძებნა.", "The archive does not contain any ORIS .tps files."),
        ["UPLOAD_UNSAFE_PATH"] = new("არქივი შეიცავს დაუშვებელ ფაილის გზას.", "The archive contains an unsafe file path."),
        ["UPLOAD_TOO_MANY_FILES"] = new("არქივში ძალიან ბევრი ფაილია.", "The archive contains too many files."),
        ["UPLOAD_TOO_LARGE_UNCOMPRESSED"] = new("არქივის გაშლილი ზომა დასაშვებზე დიდია.", "The archive expands to more data than allowed."),
        ["UPLOAD_SUSPICIOUS_COMPRESSION"] = new("არქივი არ გამოიყურება როგორც ORIS-ის ბაზა.", "The archive does not look like an ORIS database."),
        ["UPLOAD_INCOMPLETE"] = new("ატვირთვა არ დასრულებულა. სცადეთ ხელახლა — ატვირთვა გაგრძელდება იქიდან, სადაც შეწყდა.", "The upload is incomplete. Try again — it will resume where it stopped."),
    };

    private static readonly Bilingual Unknown = new("მოხდა შეცდომა. სცადეთ მოგვიანებით.", "Something went wrong. Please try again later.");

    public static Bilingual Error(string? code)
    {
        if (code is not null && Errors.TryGetValue(code, out var text)) return text;
        return Messages.TryProcessingFailure(code, out var reason) ? new(reason.Ka, reason.En) : Unknown;
    }

    public static Bilingual Job(JobStatus? status) => status switch
    {
        JobStatus.Queued => new("ფაილი მიღებულია და რიგშია დასამუშავებლად.", "File received and waiting to be processed."),
        JobStatus.Processing => new("ფაილი მუშავდება…", "Processing your file…"),
        JobStatus.Succeeded => new("დამუშავება დასრულდა. მონაცემები ბანკისთვის ხელმისაწვდომია.", "Processing completed. The data is now available to the bank."),
        JobStatus.Failed => new("დამუშავება ვერ მოხერხდა. შეგიძლიათ ატვირთოთ ფაილი ხელახლა.", "Processing failed. You can upload the file again."),
        _ => new("ფაილი ჯერ არ ატვირთულა.", "No file has been uploaded yet."),
    };
}
