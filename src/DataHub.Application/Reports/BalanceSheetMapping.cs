namespace DataHub.Application.Reports;

/// <summary>
/// Which accounts make up each balance-sheet line. Every account balance is assigned to the line
/// whose prefix is the longest match, so the class-level catch-alls (<c>"1"</c>, <c>"3"</c>…) keep
/// the sheet balanced even for accounts a company added itself.
/// </summary>
/// <remarks>
/// Lines under <c>assets</c> are debit balances (debit − credit); contra accounts such as depreciation
/// therefore come out negative, and each <c>…Total</c> line is the plain sum. All other lines are credit
/// balances (credit − debit). Classes 6–9 (income and expenses not yet closed) form <c>profitLossForPeriod</c>.
/// The defaults follow the Georgian chart of accounts as used by ORIS; entries in the <c>Reports:BalanceSheet</c>
/// configuration section replace the default prefixes of the lines they name.
/// </remarks>
public sealed class BalanceSheetOptions
{
    public const string Section = "Reports:BalanceSheet";

    public Dictionary<string, string[]> Lines { get; set; } = new(Default);

    public static readonly IReadOnlyDictionary<string, string[]> Default = new Dictionary<string, string[]>
    {
        // Current assets (class 1)
        ["assets.currentAssets.cash"] = ["11"],
        ["assets.currentAssets.bankAccounts"] = ["12"],
        ["assets.currentAssets.tradeReceivables"] = ["141"],
        ["assets.currentAssets.doubtfulDebtAllowance"] = ["1415"],
        ["assets.currentAssets.inventories"] = ["16"],
        ["assets.currentAssets.rawMaterials"] = ["162"],
        ["assets.currentAssets.otherCurrentAssets"] = ["1", "13", "19"],
        ["assets.currentAssets.otherShortTermReceivables"] = ["1420", "1440", "1470", "1490", "15", "1890"],
        ["assets.currentAssets.receivablesFromStaff"] = ["143"],
        ["assets.currentAssets.receivablesFromLoansGiven"] = ["1450", "1455"],
        ["assets.currentAssets.currentPortionOfCapital"] = ["1460"],
        ["assets.currentAssets.advancesToSuppliers"] = ["148"],
        ["assets.currentAssets.prepaidExpenses"] = ["17"],
        ["assets.currentAssets.dividendsReceivable"] = ["1810"],
        ["assets.currentAssets.interestReceivable"] = ["1820"],

        // Fixed assets (21xx cost, 22xx accumulated depreciation)
        ["assets.fixedAssets.land"] = ["2110", "2210"],
        ["assets.fixedAssets.constructionInProgress"] = ["2120", "2220"],
        ["assets.fixedAssets.buildings"] = ["2130", "2140"],
        ["assets.fixedAssets.buildingsAccumDep"] = ["2230", "2240"],
        ["assets.fixedAssets.machineryEquipment"] = ["215"],
        ["assets.fixedAssets.machineryAccumDep"] = ["225"],
        ["assets.fixedAssets.furnitureOffice"] = ["2160", "2170"],
        ["assets.fixedAssets.furnitureAccumDep"] = ["2260", "2270"],
        ["assets.fixedAssets.vehicles"] = ["2180"],
        ["assets.fixedAssets.vehiclesAccumDep"] = ["2280"],
        ["assets.fixedAssets.leaseholdImprovements"] = ["2190"],
        ["assets.fixedAssets.leaseholdImprAccumDep"] = ["2290"],
        ["assets.fixedAssets.otherFixed"] = ["2", "21", "2199"],
        ["assets.fixedAssets.otherFixedAccumDep"] = ["22", "2291"],

        // Long-term receivables and investments
        ["assets.longTermReceivablesAndInvestments.longTermReceivables"] = ["23"],
        ["assets.longTermReceivablesAndInvestments.longTermInvestments"] = ["24"],

        // Intangibles (25xx cost, 26xx amortisation)
        ["assets.intangibleAssets.licensePatent"] = ["2510", "2520", "2530"],
        ["assets.intangibleAssets.licensePatentAmort"] = ["2610", "2620", "2630"],
        ["assets.intangibleAssets.goodwill"] = ["2540"],
        ["assets.intangibleAssets.goodwillAmort"] = ["2640"],
        ["assets.intangibleAssets.otherIntangibles"] = ["25"],
        ["assets.intangibleAssets.otherIntangiblesAmort"] = ["26"],

        // No standard ORIS accounts; reported as null unless configured.
        ["assets.biologicalAssets.biologicalAssetsAmount"] = [],
        ["assets.biologicalAssets.biologicalRevaluationModel"] = [],

        // Current liabilities (class 3)
        ["liabilities.currentLiabilities.tradePayables"] = ["311"],
        ["liabilities.currentLiabilities.accruedTaxes"] = ["33"],
        ["liabilities.currentLiabilities.accruedWages"] = ["3130"],
        ["liabilities.currentLiabilities.payablesToIndividuals"] = ["3131"],
        ["liabilities.currentLiabilities.deferredRevenueShortTerm"] = ["3120"],
        ["liabilities.currentLiabilities.payablesToStaff"] = ["316"],
        ["liabilities.currentLiabilities.shortTermLoansLegal"] = ["3210", "3230"],
        ["liabilities.currentLiabilities.shortTermLoansIndividual"] = [],
        ["liabilities.currentLiabilities.shortTermLoansPartners"] = ["3220"],
        ["liabilities.currentLiabilities.interestPayable"] = ["3410"],
        ["liabilities.currentLiabilities.dividendsPayable"] = ["3420"],
        ["liabilities.currentLiabilities.otherCurrentLiabilities"] = ["3"],

        // Long-term liabilities (class 4)
        ["liabilities.longTermLiabilities.longTermLoansLegal"] = ["41"],
        ["liabilities.longTermLiabilities.longTermLoansIndividual"] = [],
        ["liabilities.longTermLiabilities.longTermLoansPartners"] = [],
        ["liabilities.longTermLiabilities.deferredTaxes"] = ["4210"],
        ["liabilities.longTermLiabilities.deferredRevenue"] = ["44"],
        ["liabilities.longTermLiabilities.otherLongTermLiabilities"] = ["4"],

        // Equity (class 5) and the unclosed result of classes 6–9
        ["equity.equityComponents.shareholdersEquity"] = ["5", "51", "52"],
        ["equity.equityComponents.retainedEarnings"] = ["5310", "5320"],
        ["equity.equityComponents.profitLossForPeriod"] = ["5330", "6", "7", "8", "9"],
        ["equity.equityComponents.reservesAndGrants"] = ["54"],
    };

    /// <summary>Line for an account code by longest matching prefix; null if no line claims it.</summary>
    internal static string? LineFor(string code, IReadOnlyDictionary<string, string> prefixToLine)
    {
        for (int length = code.Length; length > 0; length--)
            if (prefixToLine.TryGetValue(code[..length], out var line)) return line;
        return null;
    }

    internal IReadOnlyDictionary<string, string> PrefixIndex()
    {
        var index = new Dictionary<string, string>();
        foreach (var (line, prefixes) in Lines)
            foreach (var prefix in prefixes)
                if (!index.TryAdd(prefix, line))
                    throw new InvalidOperationException($"Balance sheet prefix '{prefix}' is mapped to both '{index[prefix]}' and '{line}'.");
        return index;
    }
}
