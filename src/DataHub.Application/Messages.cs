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

    public static (string Subject, string Body) ProcessingFailedEmail(string companyName, string reason) =>
        ("TBC DataHub – ფაილის დამუშავება ვერ მოხერხდა / File processing failed",
         $"""
          {companyName}-ის ატვირთული ფაილის დამუშავება ვერ მოხერხდა: {reason}
          გთხოვთ, ატვირთოთ ფაილი ხელახლა იმავე ბმულით.

          The file uploaded for {companyName} could not be processed: {reason}
          Please upload the file again using the same link.
          """);
}
