namespace DataHub.Application;

/// <summary>Customer-facing notification texts (Georgian first, English second).</summary>
public static class Messages
{
    public static string InvitationSms(string link) =>
        $"TBC: ატვირთეთ ORIS-ის ბაზა სესხის განაცხადისთვის / Upload your ORIS data: {link}";

    public static (string Subject, string Body) InvitationEmail(string companyName, string link, DateTimeOffset expiresAt) =>
        ("TBC DataHub – ORIS მონაცემების ატვირთვა / ORIS data upload",
         $"""
          გამარჯობა,

          {companyName}-ის სესხის განაცხადისთვის გთხოვთ, ატვირთოთ ORIS-ის ბაზა ამ ბმულით:
          {link}

          ბმული მოქმედებს {expiresAt:dd.MM.yyyy}-მდე.

          ---
          Hello,

          To continue {companyName}'s loan application, please upload your ORIS data using this link:
          {link}

          The link is valid until {expiresAt:yyyy-MM-dd}.
          """);

    public static string OtpSms(string code) =>
        $"TBC DataHub: ვერიფიკაციის კოდი / verification code: {code}";

    public static (string Subject, string Body) OtpEmail(string code, TimeSpan lifetime) =>
        ("TBC DataHub – ვერიფიკაციის კოდი / Verification code",
         $"""
          თქვენი ვერიფიკაციის კოდია: {code}
          კოდი მოქმედებს {lifetime.TotalMinutes:0} წუთის განმავლობაში.

          Your verification code is: {code}
          It is valid for {lifetime.TotalMinutes:0} minutes.
          """);

    public static (string Subject, string Body) ProcessingFailedEmail(string companyName, string errorCode)
    {
        var (ka, en) = ProcessingFailure(errorCode);
        return ("TBC DataHub – ფაილის დამუშავება ვერ მოხერხდა / File processing failed",
            $"""
             {companyName}-ის ატვირთული ფაილის დამუშავება ვერ მოხერხდა: {ka}
             გთხოვთ, ატვირთოთ ფაილი ხელახლა იმავე ბმულით.

             The file uploaded for {companyName} could not be processed: {en}
             Please upload the file again using the same link.
             """);
    }

    public static string ProcessingFailedSms() =>
        "TBC DataHub: ფაილის დამუშავება ვერ მოხერხდა, ატვირთეთ ხელახლა იმავე ბმულით / Processing failed, please upload again using the same link.";

    // Reasons the worker reports; kept free of internal detail, which is logged only.
    private static readonly Dictionary<string, (string Ka, string En)> Failures = new()
    {
        [ProcessingErrors.NoJournal] = ("არქივში ORIS-ის გატარებების ფაილი (WIRING.TPS) ვერ მოიძებნა.", "The archive does not contain the ORIS journal file (WIRING.TPS)."),
        [ProcessingErrors.MultipleDatabases] = ("არქივში რამდენიმე კომპანიის ბაზაა. ატვირთეთ მხოლოდ ერთი კომპანიის ბაზა.", "The archive contains more than one company database. Please upload one company only."),
        [ProcessingErrors.Unreadable] = ("ORIS-ის ფაილის წაკითხვა ვერ მოხერხდა (შესაძლოა დაზიანებული ან დაშიფრულია).", "The ORIS file could not be read (it may be damaged or encrypted)."),
        [ProcessingErrors.EmptyJournal] = ("ORIS-ის ბაზაში გატარებები არ არის.", "The ORIS database contains no journal entries."),
        [ProcessingErrors.Corrupted] = ("ფაილი ატვირთვისას დაზიანდა.", "The file was damaged during upload."),
        [ProcessingErrors.NotZip] = ("ფაილი არ არის ZIP არქივი.", "The file is not a valid ZIP archive."),
        [ProcessingErrors.TypeNotSupported] = ("ამ ტიპის ფაილის დამუშავება ჯერ ხელმისაწვდომი არ არის.", "Processing this type of file is not available yet."),
        [ProcessingErrors.Failed] = ("ტექნიკური შეფერხება. სცადეთ მოგვიანებით.", "A technical problem occurred. Please try again later."),
    };

    public static bool TryProcessingFailure(string? code, out (string Ka, string En) reason)
    {
        reason = default;
        return code is not null && Failures.TryGetValue(code, out reason);
    }

    public static (string Ka, string En) ProcessingFailure(string? code) =>
        TryProcessingFailure(code, out var reason) ? reason : Failures[ProcessingErrors.Failed];
}

/// <summary>Stable error codes recorded on failed processing jobs.</summary>
public static class ProcessingErrors
{
    public const string NoJournal = "ORIS_NO_JOURNAL";
    public const string MultipleDatabases = "ORIS_MULTIPLE_DATABASES";
    public const string Unreadable = "ORIS_UNREADABLE";
    public const string EmptyJournal = "ORIS_EMPTY_JOURNAL";
    public const string Corrupted = "UPLOAD_CORRUPTED";
    public const string NotZip = "UPLOAD_NOT_ZIP";
    public const string TypeNotSupported = "PROCESSING_TYPE_NOT_SUPPORTED";
    public const string Failed = "PROCESSING_FAILED";
}
