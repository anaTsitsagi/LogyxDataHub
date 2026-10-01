using System.Linq.Expressions;
using DataHub.Domain;
using DataHub.Oris;

namespace DataHub.Application.Reports;

/// <summary>
/// An <c>accountNumber</c> filter, accepted in ORIS form (<c>"3 3 40"</c>, <c>"1410 1 267"</c>) or compact form (<c>"3340"</c>).
/// </summary>
/// <remarks>
/// Codes follow the Georgian chart of accounts: <c>X000</c> is a class and matches every account starting
/// with X, <c>XY00</c> is a group and matches every account starting with XY, anything else is one account.
/// A sub-account path matches that sub-account and everything nested under it.
/// </remarks>
public sealed record AccountFilter(string CodePrefix, string Sub)
{
    public static AccountFilter Parse(string value)
    {
        if (!OrisAccount.TryParse(value, out var account))
            throw new DataHubException(ErrorKind.Validation, "ACCOUNT_INVALID",
                $"'{value}' is not a valid account number. Use e.g. 1210, \"1 2 10\" or \"1410 1 267\".");

        var code = account!.Code;
        var prefix = account.SubAccounts.Count > 0 ? code
            : code.EndsWith("000", StringComparison.Ordinal) ? code[..1]
            : code.EndsWith("00", StringComparison.Ordinal) ? code[..2]
            : code;
        return new AccountFilter(prefix, account.Sub);
    }

    /// <summary>The report key this filter selects (e.g. <c>1400</c>, <c>1410 1</c>).</summary>
    public string Key => Sub.Length > 0 ? $"{CodePrefix} {Sub}" : CodePrefix.PadRight(4, '0');

    public bool Matches(string code, string sub) =>
        code.StartsWith(CodePrefix, StringComparison.Ordinal)
        && (Sub.Length == 0 || sub == Sub || sub.StartsWith(Sub + " ", StringComparison.Ordinal));

    /// <summary>SQL-translatable predicate: the line's debit or credit side matches any of the filters.</summary>
    public static Expression<Func<JournalEntry, bool>> AnySide(IReadOnlyList<AccountFilter> filters)
    {
        var line = Expression.Parameter(typeof(JournalEntry), "j");
        Expression? body = null;
        foreach (var f in filters)
        {
            var debit = Side(line, nameof(JournalEntry.Debet), nameof(JournalEntry.DebetSub), f);
            var credit = Side(line, nameof(JournalEntry.Credit), nameof(JournalEntry.CreditSub), f);
            var either = Expression.OrElse(debit, credit);
            body = body is null ? either : Expression.OrElse(body, either);
        }
        return Expression.Lambda<Func<JournalEntry, bool>>(body ?? Expression.Constant(true), line);
    }

    private static readonly System.Reflection.MethodInfo StartsWith =
        typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    private static Expression Side(ParameterExpression line, string codeProperty, string subProperty, AccountFilter f)
    {
        var code = Expression.Property(line, codeProperty);
        Expression match = Expression.AndAlso(
            Expression.NotEqual(code, Expression.Constant(null, typeof(string))),
            Expression.Call(code, StartsWith, Expression.Constant(f.CodePrefix)));
        if (f.Sub.Length > 0)
        {
            var sub = Expression.Property(line, subProperty);
            match = Expression.AndAlso(match, Expression.OrElse(
                Expression.Equal(sub, Expression.Constant(f.Sub)),
                Expression.Call(sub, StartsWith, Expression.Constant(f.Sub + " "))));
        }
        return match;
    }
}
