namespace DataHub.Application.Reports;

// Shape and names follow "json responces/balance_sheet_response_en.json" of the API contract.
// Amounts are strings with two decimals; null means no account of that line has any postings.

public sealed record BalanceSheet(string Title, DateOnly AsOf, string? Currency, BalanceSheetAssets Assets,
    BalanceSheetLiabilities Liabilities, BalanceSheetEquity Equity, BalanceSheetTotals Totals);

public sealed record BalanceSheetAssets(CurrentAssets CurrentAssets, FixedAssets FixedAssets,
    LongTermReceivablesAndInvestments LongTermReceivablesAndInvestments, IntangibleAssets IntangibleAssets,
    BiologicalAssets BiologicalAssets, AssetTotals Totals);

public sealed record CurrentAssets(string? Cash, string? BankAccounts, string? TradeReceivables, string? DoubtfulDebtAllowance,
    string? ReceivablesTotal, string? Inventories, string? RawMaterials, string? OtherCurrentAssets, string? OtherShortTermReceivables,
    string? ReceivablesFromStaff, string? ReceivablesFromLoansGiven, string? CurrentPortionOfCapital, string? AdvancesToSuppliers,
    string? PrepaidExpenses, string? DividendsReceivable, string? InterestReceivable);

public sealed record FixedAssets(string? Land, string? ConstructionInProgress,
    string? Buildings, string? BuildingsAccumDep, string? BuildingsTotal,
    string? MachineryEquipment, string? MachineryAccumDep, string? MachineryTotal,
    string? FurnitureOffice, string? FurnitureAccumDep, string? FurnitureTotal,
    string? Vehicles, string? VehiclesAccumDep, string? VehiclesTotal,
    string? LeaseholdImprovements, string? LeaseholdImprAccumDep, string? LeaseholdImprTotal,
    string? OtherFixed, string? OtherFixedAccumDep, string? OtherFixedTotal);

public sealed record LongTermReceivablesAndInvestments(string? LongTermReceivables, string? LongTermInvestments);

public sealed record IntangibleAssets(string? LicensePatent, string? LicensePatentAmort, string? LicensePatentTotal,
    string? Goodwill, string? GoodwillAmort, string? GoodwillTotal,
    string? OtherIntangibles, string? OtherIntangiblesAmort, string? OtherIntangiblesTotal);

public sealed record BiologicalAssets(string? BiologicalAssetsAmount, string? BiologicalRevaluationModel);

public sealed record AssetTotals(string? TotalCurrentAssets, string? TotalFixedAssets, string? TotalIntangibleAssets, string? TotalAssets);

public sealed record BalanceSheetLiabilities(CurrentLiabilities CurrentLiabilities, LongTermLiabilities LongTermLiabilities, LiabilityTotals Totals);

public sealed record CurrentLiabilities(string? TradePayables, string? AccruedTaxes, string? AccruedWages, string? PayablesToIndividuals,
    string? DeferredRevenueShortTerm, string? PayablesToStaff, string? ShortTermLoansLegal, string? ShortTermLoansIndividual,
    string? ShortTermLoansPartners, string? InterestPayable, string? DividendsPayable, string? OtherCurrentLiabilities);

public sealed record LongTermLiabilities(string? LongTermLoansLegal, string? LongTermLoansIndividual, string? LongTermLoansPartners,
    string? DeferredTaxes, string? DeferredRevenue, string? OtherLongTermLiabilities);

public sealed record LiabilityTotals(string? TotalCurrentLiabilities, string? TotalLongTermLiabilities, string? TotalLiabilities);

public sealed record BalanceSheetEquity(EquityComponents EquityComponents, EquityTotals Totals);

public sealed record EquityComponents(string? ShareholdersEquity, string? RetainedEarnings, string? ProfitLossForPeriod, string? ReservesAndGrants);

public sealed record EquityTotals(string? TotalEquity);

public sealed record BalanceSheetTotals(string? TotalLiabilitiesAndEquity);

/// <summary>Builds the balance sheet from per-line amounts, computing the subtotal lines.</summary>
internal static class BalanceSheetBuilder
{
    public static BalanceSheet Build(DateOnly asOf, string? currency, IReadOnlyDictionary<string, decimal?> lines)
    {
        decimal? L(string key) => lines.GetValueOrDefault(key);
        static decimal? Sum(params decimal?[] values) =>
            values.Any(v => v.HasValue) ? values.Sum(v => v ?? 0) : null;
        static string? F(decimal? v) => Money.Format(v);

        const string ca = "assets.currentAssets.", fa = "assets.fixedAssets.", lt = "assets.longTermReceivablesAndInvestments.",
            ia = "assets.intangibleAssets.", ba = "assets.biologicalAssets.", cl = "liabilities.currentLiabilities.",
            ll = "liabilities.longTermLiabilities.", eq = "equity.equityComponents.";

        string[] currentKeys = ["cash", "bankAccounts", "tradeReceivables", "doubtfulDebtAllowance", "inventories", "rawMaterials",
            "otherCurrentAssets", "otherShortTermReceivables", "receivablesFromStaff", "receivablesFromLoansGiven",
            "currentPortionOfCapital", "advancesToSuppliers", "prepaidExpenses", "dividendsReceivable", "interestReceivable"];
        var totalCurrent = Sum([.. currentKeys.Select(k => L(ca + k))]);

        decimal? Net(string cost, string dep) => Sum(L(fa + cost), L(fa + dep));
        var buildings = Net("buildings", "buildingsAccumDep");
        var machinery = Net("machineryEquipment", "machineryAccumDep");
        var furniture = Net("furnitureOffice", "furnitureAccumDep");
        var vehicles = Net("vehicles", "vehiclesAccumDep");
        var leasehold = Net("leaseholdImprovements", "leaseholdImprAccumDep");
        var otherFixed = Net("otherFixed", "otherFixedAccumDep");
        var totalFixed = Sum(L(fa + "land"), L(fa + "constructionInProgress"), buildings, machinery, furniture, vehicles, leasehold, otherFixed);

        var license = Sum(L(ia + "licensePatent"), L(ia + "licensePatentAmort"));
        var goodwill = Sum(L(ia + "goodwill"), L(ia + "goodwillAmort"));
        var otherIntangibles = Sum(L(ia + "otherIntangibles"), L(ia + "otherIntangiblesAmort"));
        var totalIntangible = Sum(license, goodwill, otherIntangibles);

        var totalAssets = Sum(totalCurrent, totalFixed, L(lt + "longTermReceivables"), L(lt + "longTermInvestments"), totalIntangible,
            L(ba + "biologicalAssetsAmount"), L(ba + "biologicalRevaluationModel"));

        string[] currentLiabilityKeys = ["tradePayables", "accruedTaxes", "accruedWages", "payablesToIndividuals", "deferredRevenueShortTerm",
            "payablesToStaff", "shortTermLoansLegal", "shortTermLoansIndividual", "shortTermLoansPartners", "interestPayable",
            "dividendsPayable", "otherCurrentLiabilities"];
        string[] longTermKeys = ["longTermLoansLegal", "longTermLoansIndividual", "longTermLoansPartners", "deferredTaxes",
            "deferredRevenue", "otherLongTermLiabilities"];
        var totalCurrentLiabilities = Sum([.. currentLiabilityKeys.Select(k => L(cl + k))]);
        var totalLongTerm = Sum([.. longTermKeys.Select(k => L(ll + k))]);
        var totalLiabilities = Sum(totalCurrentLiabilities, totalLongTerm);

        var totalEquity = Sum(L(eq + "shareholdersEquity"), L(eq + "retainedEarnings"), L(eq + "profitLossForPeriod"), L(eq + "reservesAndGrants"));

        return new BalanceSheet("Balance Sheet", asOf, currency,
            new BalanceSheetAssets(
                new CurrentAssets(F(L(ca + "cash")), F(L(ca + "bankAccounts")), F(L(ca + "tradeReceivables")),
                    F(L(ca + "doubtfulDebtAllowance")), F(Sum(L(ca + "tradeReceivables"), L(ca + "doubtfulDebtAllowance"))),
                    F(L(ca + "inventories")), F(L(ca + "rawMaterials")), F(L(ca + "otherCurrentAssets")),
                    F(L(ca + "otherShortTermReceivables")), F(L(ca + "receivablesFromStaff")), F(L(ca + "receivablesFromLoansGiven")),
                    F(L(ca + "currentPortionOfCapital")), F(L(ca + "advancesToSuppliers")), F(L(ca + "prepaidExpenses")),
                    F(L(ca + "dividendsReceivable")), F(L(ca + "interestReceivable"))),
                new FixedAssets(F(L(fa + "land")), F(L(fa + "constructionInProgress")),
                    F(L(fa + "buildings")), F(L(fa + "buildingsAccumDep")), F(buildings),
                    F(L(fa + "machineryEquipment")), F(L(fa + "machineryAccumDep")), F(machinery),
                    F(L(fa + "furnitureOffice")), F(L(fa + "furnitureAccumDep")), F(furniture),
                    F(L(fa + "vehicles")), F(L(fa + "vehiclesAccumDep")), F(vehicles),
                    F(L(fa + "leaseholdImprovements")), F(L(fa + "leaseholdImprAccumDep")), F(leasehold),
                    F(L(fa + "otherFixed")), F(L(fa + "otherFixedAccumDep")), F(otherFixed)),
                new LongTermReceivablesAndInvestments(F(L(lt + "longTermReceivables")), F(L(lt + "longTermInvestments"))),
                new IntangibleAssets(F(L(ia + "licensePatent")), F(L(ia + "licensePatentAmort")), F(license),
                    F(L(ia + "goodwill")), F(L(ia + "goodwillAmort")), F(goodwill),
                    F(L(ia + "otherIntangibles")), F(L(ia + "otherIntangiblesAmort")), F(otherIntangibles)),
                new BiologicalAssets(F(L(ba + "biologicalAssetsAmount")), F(L(ba + "biologicalRevaluationModel"))),
                new AssetTotals(F(totalCurrent), F(totalFixed), F(totalIntangible), F(totalAssets))),
            new BalanceSheetLiabilities(
                new CurrentLiabilities(F(L(cl + "tradePayables")), F(L(cl + "accruedTaxes")), F(L(cl + "accruedWages")),
                    F(L(cl + "payablesToIndividuals")), F(L(cl + "deferredRevenueShortTerm")), F(L(cl + "payablesToStaff")),
                    F(L(cl + "shortTermLoansLegal")), F(L(cl + "shortTermLoansIndividual")), F(L(cl + "shortTermLoansPartners")),
                    F(L(cl + "interestPayable")), F(L(cl + "dividendsPayable")), F(L(cl + "otherCurrentLiabilities"))),
                new LongTermLiabilities(F(L(ll + "longTermLoansLegal")), F(L(ll + "longTermLoansIndividual")),
                    F(L(ll + "longTermLoansPartners")), F(L(ll + "deferredTaxes")), F(L(ll + "deferredRevenue")),
                    F(L(ll + "otherLongTermLiabilities"))),
                new LiabilityTotals(F(totalCurrentLiabilities), F(totalLongTerm), F(totalLiabilities))),
            new BalanceSheetEquity(
                new EquityComponents(F(L(eq + "shareholdersEquity")), F(L(eq + "retainedEarnings")),
                    F(L(eq + "profitLossForPeriod")), F(L(eq + "reservesAndGrants"))),
                new EquityTotals(F(totalEquity))),
            new BalanceSheetTotals(F(Sum(totalLiabilities, totalEquity))));
    }
}
