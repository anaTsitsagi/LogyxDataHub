using LogyxDataHub.Data;
using LogyxDataHub.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
            [FromHeader(Name = "X-Tenant-Id")] string? headerTenantId,
            [FromQuery] DateTime? fromDate,
            [FromQuery] DateTime? toDate,
            [FromQuery] List<string>? accountNumber,
            [FromQuery] string? currency, // CSV
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100)
        {
            if (string.IsNullOrWhiteSpace(authorization) || !authorization.StartsWith("Bearer "))
                return Unauthorized();
            if (string.IsNullOrWhiteSpace(headerTenantId)) return BadRequest("Missing X-Tenant-Id header.");
            if (!fromDate.HasValue || !toDate.HasValue) return BadRequest("fromDate and toDate are required.");

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 500);

            var currencySet = (currency ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var accs = accountNumber != null && accountNumber.Count > 0
                ? accountNumber.Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList()
                : null;

            var fromDay = fromDate.Value.Date;
            var toDayExclusive = toDate.Value.Date.AddDays(1);

            IQueryable<HiroWiring> ApplyCurrencyFilter(IQueryable<HiroWiring> q)
            {
                if (currencySet.Count > 0)
                    q = q.Where(x => currencySet.Contains(x.Currency ?? string.Empty));
                return q;
            }

            try
            {
                var beforeFromQuery = _db.HiroWirings.AsNoTracking()
                    .Where(x => x.OperationDate.HasValue && x.OperationDate.Value < fromDay);
                beforeFromQuery = ApplyCurrencyFilter(beforeFromQuery);

                var upToToDateQuery = _db.HiroWirings.AsNoTracking()
                    .Where(x => x.OperationDate.HasValue && x.OperationDate.Value < toDayExclusive);
                upToToDateQuery = ApplyCurrencyFilter(upToToDateQuery);

                // Compute sums on DB as double (matching float), then convert to decimal for accurate money arithmetic
                double startDebetDouble, startCreditDouble, endDebetDouble, endCreditDouble;

                if (accs != null && accs.Count > 0)
                {
                    startDebetDouble = await beforeFromQuery.Where(x => accs.Contains(x.Debet ?? string.Empty)).SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;
                    startCreditDouble = await beforeFromQuery.Where(x => accs.Contains(x.Credit ?? string.Empty)).SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;
                    endDebetDouble = await upToToDateQuery.Where(x => accs.Contains(x.Debet ?? string.Empty)).SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;
                    endCreditDouble = await upToToDateQuery.Where(x => accs.Contains(x.Credit ?? string.Empty)).SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;
                }
                else
                {
                    startDebetDouble = await beforeFromQuery.Where(x => !string.IsNullOrEmpty(x.Debet)).SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;
                    startCreditDouble = await beforeFromQuery.Where(x => !string.IsNullOrEmpty(x.Credit)).SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;
                    endDebetDouble = await upToToDateQuery.Where(x => !string.IsNullOrEmpty(x.Debet)).SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;
                    endCreditDouble = await upToToDateQuery.Where(x => !string.IsNullOrEmpty(x.Credit)).SumAsync(x => (double?)(x.Amount ?? 0)) ?? 0.0;
                }

                // Convert to decimal for final balances
                decimal startDebet = Convert.ToDecimal(startDebetDouble);
                decimal startCredit = Convert.ToDecimal(startCreditDouble);
                decimal endDebet = Convert.ToDecimal(endDebetDouble);
                decimal endCredit = Convert.ToDecimal(endCreditDouble);

                decimal startBalance = startDebet - startCredit;
                decimal endBalance = endDebet - endCredit;

                // Fetch page of entities first, then map amounts to decimal for JSON
                var itemsQuery = _db.HiroWirings.AsNoTracking()
                    .Where(x => x.OperationDate.HasValue && x.OperationDate.Value >= fromDay && x.OperationDate.Value < toDayExclusive);
                itemsQuery = ApplyCurrencyFilter(itemsQuery);

                if (accs != null && accs.Count > 0)
                {
                    itemsQuery = itemsQuery.Where(x => accs.Contains(x.Debet ?? string.Empty) || accs.Contains(x.Credit ?? string.Empty) ||
                                                       accs.Contains(x.DebetSub ?? string.Empty) || accs.Contains(x.CreditSub ?? string.Empty));
                }

                var rawItems = await itemsQuery
                    .OrderBy(x => x.OperationDate)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var items = rawItems.Select(x => new {
                    documentNumber = x.DocumentNumber,
                    entryNumber = x.EntryNumber,
                    debet = x.Debet,
                    debetSub = x.DebetSub,
                    credit = x.Credit,
                    creditSub = x.CreditSub,
                    // use AmountDecimal for safe decimal representation
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
                    items
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetJournalEntries");
                return StatusCode(500, "Failed to read data from HIRO_WIRING.");
            }
        }
    }
}