using LogyxDataHub.Data;
using LogyxDataHub.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace LogyxDataHub.Controllers
{
    [ApiController]
    [Route("reports")]
    [Authorize]
    public class HiroWiringController : ControllerBase
    {
        private readonly LogyxDbContext _db;
        private readonly ILogger<HiroWiringController> _logger;

        public HiroWiringController(LogyxDbContext db, ILogger<HiroWiringController> logger)
        {
            _db = db;
            _logger = logger;
        }

        [HttpGet("journal-entries")]
        public async Task<IActionResult> GetJournalEntries(
            [FromHeader(Name = "Authorization")] string? authorization,
            [FromHeader(Name = "X-Identity")] string? headerIdentity,
            [FromQuery(Name = "debit")] List<string>? debit,   // optional: one or more debit accounts
            [FromQuery(Name = "credit")] List<string>? credit, // optional: one or more credit accounts
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] string? currency, // CSV
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100)
        {
            if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer "))
                return Unauthorized();
            if (string.IsNullOrWhiteSpace(headerIdentity)) return BadRequest("Missing X-Identity header.");
            if (!fromDate.HasValue || !toDate.HasValue) return BadRequest("fromDate and toDate are required.");

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var currencySet = (currency ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // normalize debit/credit lists
            var debitList = debit?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var creditList = credit?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // date boundaries: fromDate inclusive, toDate inclusive (use exclusive end)
            var fromDay = fromDate.Value.Date;
            var toDayExclusive = toDate.Value.Date;

            // Helper to apply currency filter (if provided)
            IQueryable<HiroWiring> ApplyCurrencyFilter(IQueryable<HiroWiring> q)
            {
                if (currencySet.Count > 0)
                    q = q.Where(x => currencySet.Contains(x.Currency ?? string.Empty));
                return q;
            }

            try
            {
                // Base query (date + optional currency)
                var baseQuery = _db.HiroWirings.AsNoTracking()
                    .Where(x => x.OperationDate.HasValue && x.OperationDate.Value >= fromDay && x.OperationDate.Value <= toDayExclusive);
                baseQuery = ApplyCurrencyFilter(baseQuery);

                // Apply debit/credit filtering:
                // - if both lists provided include rows that match either side (credit OR debit)
                // - if only one provided apply that filter
                if ((debitList != null && debitList.Count > 0) || (creditList != null && creditList.Count > 0))
                {
                    baseQuery = baseQuery.Where(x =>
                        (debitList != null && debitList.Count > 0 && (debitList.Contains(x.Debet ?? string.Empty) || debitList.Contains(x.DebetSub ?? string.Empty)))
                        ||
                        (creditList != null && creditList.Count > 0 && (creditList.Contains(x.Credit ?? string.Empty) || creditList.Contains(x.CreditSub ?? string.Empty)))
                    );
                }

                // Total count for pagination metadata (server-side COUNT)
                var totalCount = await baseQuery.CountAsync();
                var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
                var pagesLeft = Math.Max(0, totalPages - page);
                var remainingPages = pagesLeft > 0 ? Enumerable.Range(page + 1, pagesLeft).ToArray() : Array.Empty<int>();

                // Fetch requested page
                var rawItems = await baseQuery
                    .OrderBy(x => x.OperationDate)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var items = rawItems.Select(x => new
                {
                    documentNumber = x.DocumentNumber,
                    entryNumber = x.EntryNumber,
                    debet = x.Debet,
                    debetSub = x.DebetSub,
                    credit = x.Credit,
                    creditSub = x.CreditSub,
                    amount = x.AmountDecimal,
                    currency = x.Currency,
                    description = x.Description,
                    quantity = x.Quantity,
                    unit = x.Unit,
                    postedBy = x.PostedBy,
                    operationDate = x.OperationDate,
                    postingDate = x.PostingDate
                }).ToList();

                return Ok(new
                {
                    items,
                    pagination = new
                    {
                        currentPage = page,
                        pageSize,
                        totalCount,
                        totalPages,
                        pagesLeft,
                        remainingPages
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetJournalEntries");
                return StatusCode(500, "Failed to read data from HIRO_WIRING.");
            }
        }


        [HttpGet("merged-journal-entries")]
        public async Task<IActionResult> GetMergedJournalEntries(
            [FromHeader(Name = "Authorization")] string? authorization,
            [FromHeader(Name = "X-Identity")] string? headerIdentity,
            [FromQuery(Name = "debit")] string? debitAccount,   // mandatory single debit account
            [FromQuery(Name = "credit")] string? creditAccount, // mandatory single credit account
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] string? currency, // CSV
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100)
        {
            if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer "))
                return Unauthorized();
            if (string.IsNullOrWhiteSpace(headerIdentity)) return BadRequest("Missing X-Identity header.");

            // Both accounts are mandatory for merged results
            if (string.IsNullOrWhiteSpace(debitAccount) || string.IsNullOrWhiteSpace(creditAccount))
                return BadRequest("Both 'debit' and 'credit' query parameters are required to return merged entries.");

            if (!fromDate.HasValue || !toDate.HasValue) return BadRequest("fromDate and toDate are required.");

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var debitAcct = debitAccount!.Trim();
            var creditAcct = creditAccount!.Trim();
            if (string.IsNullOrEmpty(debitAcct) || string.IsNullOrEmpty(creditAcct))
                return BadRequest("'debit' and 'credit' cannot be empty.");

            var currencySet = (currency ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // date boundaries: fromDate inclusive, toDate inclusive (use exclusive end)
            var fromDay = fromDate.Value.Date;
            var toDayExclusive = toDate.Value.Date;

            // Helper to apply currency filter (if provided)
            IQueryable<HiroWiring> ApplyCurrencyFilter(IQueryable<HiroWiring> q)
            {
                if (currencySet.Count > 0)
                    q = q.Where(x => currencySet.Contains(x.Currency ?? string.Empty));
                return q;
            }

            try
            {
                // Base query (date + optional currency)
                var baseQuery = _db.HiroWirings.AsNoTracking()
                    .Where(x => x.OperationDate.HasValue && x.OperationDate.Value >= fromDay && x.OperationDate.Value <= toDayExclusive);
                baseQuery = ApplyCurrencyFilter(baseQuery);

                // Require both accounts to be present in the entry (debit AND credit).
                // Include reverse pairing so caller can pass accounts in any order and still get the pair.
                baseQuery = baseQuery.Where(x =>
                    (
                        ((x.Debet ?? string.Empty) == debitAcct || (x.DebetSub ?? string.Empty) == debitAcct)
                        &&
                        ((x.Credit ?? string.Empty) == creditAcct || (x.CreditSub ?? string.Empty) == creditAcct)
                    )
                    ||
                    (
                        ((x.Debet ?? string.Empty) == creditAcct || (x.DebetSub ?? string.Empty) == creditAcct)
                        &&
                        ((x.Credit ?? string.Empty) == debitAcct || (x.CreditSub ?? string.Empty) == debitAcct)
                    )
                );

                // Total count for pagination metadata (server-side COUNT)
                var totalCount = await baseQuery.CountAsync();
                var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
                var pagesLeft = Math.Max(0, totalPages - page);
                var remainingPages = pagesLeft > 0 ? Enumerable.Range(page + 1, pagesLeft).ToArray() : Array.Empty<int>();

                // Fetch requested page
                var rawItems = await baseQuery
                    .OrderBy(x => x.OperationDate)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var items = rawItems.Select(x => new
                {
                    documentNumber = x.DocumentNumber,
                    entryNumber = x.EntryNumber,
                    debet = x.Debet,
                    debetSub = x.DebetSub,
                    credit = x.Credit,
                    creditSub = x.CreditSub,
                    amount = x.AmountDecimal,
                    currency = x.Currency,
                    description = x.Description,
                    quantity = x.Quantity,
                    unit = x.Unit,
                    postedBy = x.PostedBy,
                    operationDate = x.OperationDate,
                    postingDate = x.PostingDate
                }).ToList();

                return Ok(new
                {
                    items,
                    pagination = new
                    {
                        currentPage = page,
                        pageSize,
                        totalCount,
                        totalPages,
                        pagesLeft,
                        remainingPages
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetMergedJournalEntries");
                return StatusCode(500, "Failed to read data from HIRO_WIRING.");
            }
        }

        // GET /reports/account-card
        // Returns items for a single account and includes balances (startBalance, endBalance)
        [HttpGet("account-card")]
        public async Task<IActionResult> GetAccountCard(
            [FromHeader(Name = "Authorization")] string? authorization,
            [FromHeader(Name = "X-Identity")] string? headerIdentity,
            [FromQuery(Name = "accountNumber")] string? accountNumber, // mandatory single value
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] string? currency, // CSV
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100)
        {
            if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer "))
                return Unauthorized();
            if (string.IsNullOrWhiteSpace(headerIdentity)) return BadRequest("Missing X-Identity header.");
            if (string.IsNullOrWhiteSpace(accountNumber)) return BadRequest("Missing required query parameter: accountNumber.");
            if (!fromDate.HasValue || !toDate.HasValue) return BadRequest("fromDate and toDate are required.");

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var acct = accountNumber!.Trim();
            if (string.IsNullOrEmpty(acct)) return BadRequest("accountNumber cannot be empty.");

            var currencySet = (currency ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // date boundaries: fromDate inclusive, toDate inclusive (use exclusive end)
            var fromDay = fromDate.Value.Date;
            var toDayExclusive = toDate.Value.Date;

            IQueryable<HiroWiring> ApplyCurrencyFilter(IQueryable<HiroWiring> q)
            {
                if (currencySet.Count > 0)
                    q = q.Where(x => currencySet.Contains(x.Currency ?? string.Empty));
                return q;
            }

            try
            {
                // Balance queries: before fromDay and up to toDate (inclusive)
                var beforeFromQuery = _db.HiroWirings.AsNoTracking()
                    .Where(x => x.OperationDate.HasValue && x.OperationDate.Value < fromDay);
                beforeFromQuery = ApplyCurrencyFilter(beforeFromQuery);

                var upToToDateQuery = _db.HiroWirings.AsNoTracking()
                    .Where(x => x.OperationDate.HasValue && x.OperationDate.Value < toDayExclusive);
                upToToDateQuery = ApplyCurrencyFilter(upToToDateQuery);

                // For this single account, debet sums are rows where Debet or DebetSub equals acct
                var startDebetDouble = await beforeFromQuery
                    .Where(x => (x.Debet ?? string.Empty) == acct || (x.DebetSub ?? string.Empty) == acct)
                    .SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;

                var startCreditDouble = await beforeFromQuery
                    .Where(x => (x.Credit ?? string.Empty) == acct || (x.CreditSub ?? string.Empty) == acct)
                    .SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;

                var endDebetDouble = await upToToDateQuery
                    .Where(x => (x.Debet ?? string.Empty) == acct || (x.DebetSub ?? string.Empty) == acct)
                    .SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;

                var endCreditDouble = await upToToDateQuery
                    .Where(x => (x.Credit ?? string.Empty) == acct || (x.CreditSub ?? string.Empty) == acct)
                    .SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;

                decimal startDebet = Convert.ToDecimal(startDebetDouble);
                decimal startCredit = Convert.ToDecimal(startCreditDouble);
                decimal endDebet = Convert.ToDecimal(endDebetDouble);
                decimal endCredit = Convert.ToDecimal(endCreditDouble);

                decimal startBalance = startDebet - startCredit;
                decimal endBalance = endDebet - endCredit;

                // Items for the account within [fromDay, toDayExclusive)
                var itemsQuery = _db.HiroWirings.AsNoTracking()
                    .Where(x => x.OperationDate.HasValue
                                && x.OperationDate.Value >= fromDay
                                && x.OperationDate.Value <= toDayExclusive
                                && (
                                    (x.Debet ?? string.Empty) == acct
                                    || (x.DebetSub ?? string.Empty) == acct
                                    || (x.Credit ?? string.Empty) == acct
                                    || (x.CreditSub ?? string.Empty) == acct
                                   ));

                itemsQuery = ApplyCurrencyFilter(itemsQuery);

                // Pagination metadata for account items
                var totalCount = await itemsQuery.CountAsync();
                var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
                var pagesLeft = Math.Max(0, totalPages - page);
                var remainingPages = pagesLeft > 0 ? Enumerable.Range(page + 1, pagesLeft).ToArray() : Array.Empty<int>();

                var rawItems = await itemsQuery
                    .OrderBy(x => x.OperationDate)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var items = rawItems.Select(x => new
                {
                    documentNumber = x.DocumentNumber,
                    entryNumber = x.EntryNumber,
                    debet = x.Debet,
                    debetSub = x.DebetSub,
                    credit = x.Credit,
                    creditSub = x.CreditSub,
                    amount = x.AmountDecimal,
                    currency = x.Currency,
                    description = x.Description,
                    quantity = x.Quantity,
                    unit = x.Unit,
                    postedBy = x.PostedBy,
                    operationDate = x.OperationDate,
                    postingDate = x.PostingDate
                }).ToList();

                return Ok(new
                {
                    balances = new
                    {
                        startBalance,
                        endBalance
                    },
                    items,
                    pagination = new
                    {
                        currentPage = page,
                        pageSize,
                        totalCount,
                        totalPages,
                        pagesLeft,
                        remainingPages
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetAccountCard");
                return StatusCode(500, "Failed to read data from HIRO_WIRING.");
            }
        }
    }
}