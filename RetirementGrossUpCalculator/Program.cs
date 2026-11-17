/* * Developer: Cave Arnold
 * AI Assistant: Gemini
 * Date: November 17, 2026
 * Version: 1.4.0
 * 
 * ==============================================================================================
 * RETIREMENT DISTRIBUTION, PENSION & ROTH CONVERSION GROSS-UP CALCULATOR
 * ==============================================================================================
 * * A Windows Forms (.NET 10) application designed to calculate the exact gross retirement 
 * distribution required to yield a target net payout after accounting for Federal and Kentucky 
 * state income tax withholdings, prior year-to-date (YTD) distributions, self-withholding 
 * monthly pension income streams, and planned Traditional-to-Roth IRA conversions.
 * 
 * * ----------------------------------------------------------------------------------------------
 * FEATURES
 * ----------------------------------------------------------------------------------------------
 * * 1. DATABASE-DRIVEN TAX ENGINE
 * - Dynamic Year Discovery: Queries [dbo].[FederalStandardDeductions], [dbo].[FederalTaxRates], 
 *   and [dbo].[KentuckyTaxRates] in the [Guyton-Klinger-Withdrawals] database, populates the 
 *   Tax Year dropdown in descending order, and automatically defaults to MAX(TaxYear).
 * - Zero Hardcoded Tax Rules: All Federal marginal brackets, Federal standard deductions, 
 *   Kentucky flat tax rates, Kentucky standard deductions, and Kentucky Schedule P pension 
 *   exclusions are hydrated directly from SQL Server.
 * * 2. SMART WITHDRAWAL DATE DEFAULTING
 * - Defaults to January 1 of the selected withdrawal tax year (e.g., 2027-01-01), or defaults 
 *   to the current system date (DateTime.Today) if the calculator is launched during the active 
 *   tax year of the withdrawal.
 * * 3. SELF-WITHHOLDING PENSION ENGINE (ROYAL BLUE THEME)
 * - Automatic Pension Withholding: Models gross monthly pension payments from which Federal and 
 *   Kentucky taxes are automatically withheld.
 * - Royal Blue Visual Grouping: All pension inputs (Monthly Gross Pension, Start Month) and 
 *   pension outputs (Annualized Gross Pension, Auto-Withheld Fed & KY Pension Taxes, and Net 
 *   Annual/Monthly Pension Payout) are styled in Royal Blue (#4169E1).
 * * 4. BRIGHT ORANGE ROTH BRACKET-FILL ANALYZER
 * - Displays in high-visibility Bright Orange (#FF8C00):
 *   * The remaining taxable headroom to fill the active Federal bracket.
 *   * The exact Roth conversion principal + Federal tax + Kentucky tax that fills the bracket 
 *     when taxes are withheld via the IRA gross-up.
 *   * The full Roth conversion amount + external Federal & KY tax cost if paid from cash.
 * * 5. WIDE NO-WRAP DARK MODE UI & FILE-ONLY SERILOG DIAGNOSTICS
 * - 1140px Wide Form & 38%/62% Output Split: Engineered to display multi-part currency strings 
 *   where all dollar amounts are $100,000.00+ on a single line with zero text wrapping.
 * - File-Only Structured Logging: Uses Serilog exclusively with a rolling daily file sink 
 *   (./logs/tax-grossup-YYYYMMDD.log).
 * 
 * * ----------------------------------------------------------------------------------------------
 * TECHNICAL REQUIREMENTS
 * ----------------------------------------------------------------------------------------------
 * * - Framework: .NET 10.0 (net10.0-windows, Windows Forms)
 * - Database: Microsoft SQL Server (LocalDB \MSSQLLocalDB) - [Guyton-Klinger-Withdrawals]
 * - NuGet Packages:
 * * Microsoft.Data.SqlClient (v6.1.8+)
 * * Serilog (v4.2.0+)
 * * Serilog.Sinks.File (v6.0.0+)
 * * Database Schema Requirements:
 * 1. Table: [dbo].[FederalStandardDeductions]
 * 2. Table: [dbo].[FederalTaxRates]
 * 3. Table: [dbo].[KentuckyTaxRates]
 * 
 * * ----------------------------------------------------------------------------------------------
 * USAGE GUIDE
 * ----------------------------------------------------------------------------------------------
 * * 1. Launch: Connects to [Guyton-Klinger-Withdrawals] on LocalDB, defaults to MAX(TaxYear), 
 *    and sets Planned Withdrawal Date to Jan 1 of that year (or Today if in that tax year).
 * 2. Configure Inputs:
 *    - Select Planned Withdrawal Date and Tax Filing Status.
 *    - Enter Prior Distributions Taken YTD ($) and Target Net Distribution ($).
 *    - In the Royal Blue inputs, enter Estimated Gross Pension Per Month ($) and Start Month.
 *    - In the Bright Orange input, enter any Planned Roth IRA Conversion ($).
 * 3. Review Breakdown & Log:
 *    - Inspect the Royal Blue rows for automatic Federal/KY pension withholdings and net pension.
 *    - Inspect the Cyan/Green rows for the required gross IRA distribution and withholdings.
 *    - Inspect the Bright Orange rows for remaining Roth conversion capacity in the active bracket.
 * * ==============================================================================================
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Data.SqlClient;
using Serilog;

namespace RetirementGrossUpCalculator
{
    /// <summary>
    /// Represents a single marginal tax bracket row from [dbo].[FederalTaxRates].
    /// </summary>
    public record TaxBracketRow(
        int TaxYear,
        string FilingStatus,
        decimal TaxRate,
        decimal IncomeMin,
        decimal? IncomeMax
    );

    /// <summary>
    /// Represents a single year's Kentucky tax rules from [dbo].[KentuckyTaxRates].
    /// </summary>
    public record KentuckyTaxRow(
        int TaxYear,
        decimal TaxRate,
        decimal StandardDeduction,
        decimal PensionExclusion
    );

    /// <summary>
    /// Aggregates all Federal and Kentucky tax parameters for a specific tax year.
    /// </summary>
    public record YearlyTaxData(
        int TaxYear,
        Dictionary<string, decimal> FederalStandardDeductions,
        Dictionary<string, List<TaxBracketRow>> FederalTaxBrackets,
        KentuckyTaxRow KentuckyRules
    );

    /// <summary>
    /// Describes the active Federal marginal tax bracket, remaining room in that bracket,
    /// and the exact additional Roth conversion + Federal & KY tax amounts required to fill it.
    /// </summary>
    public record FederalBracketAndRothFillStatus(
        decimal TotalAnnualGrossIncome,
        decimal FederalStandardDeduction,
        decimal TotalFederalTaxableIncome,
        decimal CurrentMarginalRate,
        decimal BracketTaxableMin,
        decimal? BracketTaxableMax,
        decimal? RemainingRoomInCurrentBracket,
        decimal? GrossIncomeCeilingForCurrentBracket,
        decimal? NextMarginalRate,
        // Scenario A: Conversion Taxes Withheld via IRA Gross-Up (Conversion + FedTax + KyTax == RemainingRoom)
        decimal AdditionalRothNetIfTaxGrossedUp,
        decimal AdditionalRothFedTaxIfGrossedUp,
        decimal AdditionalRothKyTaxIfGrossedUp,
        // Scenario B: Conversion Taxes Paid from External Cash (Conversion == RemainingRoom)
        decimal AdditionalRothPrincipalIfTaxPaidExternally,
        decimal AdditionalRothFedTaxIfPaidExternally,
        decimal AdditionalRothKyTaxIfPaidExternally
    );

    /// <summary>
    /// Immutable result payload returned by the iterative tax gross-up solver.
    /// Includes self-withholding pension metrics and IRA distribution gross-up metrics.
    /// </summary>
    public record GrossUpCalculationResult(
        int TaxYear,
        DateTime PlannedWithdrawalDate,
        string IrsEstimatedTaxQuarter,
        string FilingStatus,
        decimal PriorDistributionsYtd,
        // Self-Withholding Pension Breakdown (Royal Blue)
        decimal MonthlyPensionGross,
        int PensionStartMonth,
        int PensionMonthsActive,
        decimal AnnualizedPensionGross,
        decimal PensionFedTaxAutoWithheld,
        decimal PensionKyTaxAutoWithheld,
        decimal PensionTotalTaxAutoWithheld,
        decimal AnnualizedPensionNet,
        decimal MonthlyPensionNet,
        // Roth & IRA Distribution Gross-Up Breakdown
        decimal PlannedRothConversion,
        decimal TargetNetDistribution,
        decimal RequiredGrossDistribution,
        decimal TotalIraGrossActivity,
        decimal DistributionBaseFedTaxWithheld,
        decimal ExtraRothFedTaxWithheld,
        decimal TotalDistributionFedTaxWithheld,
        decimal DistributionBaseKyTaxWithheld,
        decimal ExtraRothKyTaxWithheld,
        decimal TotalDistributionKyTaxWithheld,
        decimal TotalDistributionTaxesWithheld,
        decimal EffectiveFedWithholdingPct,
        decimal EffectiveKyWithholdingPct,
        decimal EffectiveTotalWithholdingPct,
        FederalBracketAndRothFillStatus BracketStatus
    );

    /// <summary>
    /// Data access repository responsible for reading Federal and Kentucky tax tables
    /// from the [Guyton-Klinger-Withdrawals] SQL Server database.
    /// </summary>
    public sealed class SqlTaxRepository
    {
        public const string DefaultConnectionString =
            @"Server=(localdb)\MSSQLLocalDB;Database=Guyton-Klinger-Withdrawals;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=10;";

        private readonly string _connectionString;

        public SqlTaxRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("SQL connection string cannot be null or empty.", nameof(connectionString));

            _connectionString = connectionString;
        }

        /// <summary>
        /// Connects to SQL Server, loads [FederalStandardDeductions], [FederalTaxRates], and
        /// [KentuckyTaxRates], and returns a dictionary keyed by TaxYear for all complete years.
        /// </summary>
        public Dictionary<int, YearlyTaxData> LoadAllTaxYears()
        {
            var deductionsByYear = new Dictionary<int, Dictionary<string, decimal>>();
            var bracketsByYear = new Dictionary<int, Dictionary<string, List<TaxBracketRow>>>();
            var kyRatesByYear = new Dictionary<int, KentuckyTaxRow>();

            try
            {
                using var conn = new SqlConnection(_connectionString);
                conn.Open();

                // 1. Query [dbo].[FederalStandardDeductions]
                const string dedSql = @"
                    SELECT [TaxYear], [FilingStatus], [DeductionAmount]
                    FROM [dbo].[FederalStandardDeductions]
                    ORDER BY [TaxYear] DESC, [FilingStatus] ASC;";

                using (var cmd = new SqlCommand(dedSql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int year = reader.GetInt32(0);
                        string status = reader.GetString(1).Trim();
                        decimal amount = reader.GetDecimal(2);

                        if (!deductionsByYear.TryGetValue(year, out var statusDict))
                        {
                            statusDict = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                            deductionsByYear[year] = statusDict;
                        }
                        statusDict[status] = amount;
                    }
                }

                // 2. Query [dbo].[FederalTaxRates]
                const string rateSql = @"
                    SELECT [TaxYear], [FilingStatus], [TaxRate], [IncomeMin], [IncomeMax]
                    FROM [dbo].[FederalTaxRates]
                    ORDER BY [TaxYear] DESC, [FilingStatus] ASC, [IncomeMin] ASC;";

                using (var cmd = new SqlCommand(rateSql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int year = reader.GetInt32(0);
                        string status = reader.GetString(1).Trim();
                        decimal rate = reader.GetDecimal(2);
                        decimal min = reader.GetDecimal(3);
                        decimal? max = reader.IsDBNull(4) ? null : reader.GetDecimal(4);

                        if (!bracketsByYear.TryGetValue(year, out var statusBrackets))
                        {
                            statusBrackets = new Dictionary<string, List<TaxBracketRow>>(StringComparer.OrdinalIgnoreCase);
                            bracketsByYear[year] = statusBrackets;
                        }

                        if (!statusBrackets.TryGetValue(status, out var list))
                        {
                            list = new List<TaxBracketRow>();
                            statusBrackets[status] = list;
                        }

                        list.Add(new TaxBracketRow(year, status, rate, min, max));
                    }
                }

                // 3. Query [dbo].[KentuckyTaxRates]
                const string kySql = @"
                    SELECT [TaxYear], [TaxRate], [StandardDeduction], [PensionExclusion]
                    FROM [dbo].[KentuckyTaxRates]
                    ORDER BY [TaxYear] DESC;";

                using (var cmd = new SqlCommand(kySql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int year = reader.GetInt32(0);
                        decimal rate = reader.GetDecimal(1);
                        decimal stdDed = reader.GetDecimal(2);
                        decimal pensionExcl = reader.GetDecimal(3);

                        kyRatesByYear[year] = new KentuckyTaxRow(year, rate, stdDed, pensionExcl);
                    }
                }

                var validYears = deductionsByYear.Keys
                    .Intersect(bracketsByYear.Keys)
                    .Intersect(kyRatesByYear.Keys)
                    .OrderByDescending(y => y)
                    .ToList();

                var result = new Dictionary<int, YearlyTaxData>();

                foreach (int year in validYears)
                {
                    result[year] = new YearlyTaxData(
                        TaxYear: year,
                        FederalStandardDeductions: deductionsByYear[year],
                        FederalTaxBrackets: bracketsByYear[year],
                        KentuckyRules: kyRatesByYear[year]
                    );
                }

                return result;
            }
            catch (SqlException sqlEx)
            {
                Log.Error(
                    sqlEx,
                    "SQL Error (Number: {ErrorNumber}, State: {State}, Server: {Server}) while loading tax tables.",
                    sqlEx.Number, sqlEx.State, sqlEx.Server);
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unexpected error in SqlTaxRepository.LoadAllTaxYears().");
                throw;
            }
        }
    }

    /// <summary>
    /// Deterministic tax calculation engine that computes automatic Federal and Kentucky
    /// pension withholdings, executes the binary search solver for IRA distributions, and
    /// evaluates how much additional Roth conversion fits in the active Federal bracket.
    /// </summary>
    public static class TaxCalculatorEngine
    {
        public static string GetIrsEstimatedTaxQuarter(DateTime withdrawalDate) =>
            withdrawalDate.Month switch
            {
                >= 1 and <= 3 => "Q1 (Jan 1 - Mar 31 | Due Apr 15)",
                >= 4 and <= 5 => "Q2 (Apr 1 - May 31 | Due Jun 15)",
                >= 6 and <= 8 => "Q3 (Jun 1 - Aug 31 | Due Sep 15)",
                _ => "Q4 (Sep 1 - Dec 31 | Due Jan 15)"
            };

        public static decimal CalculateCumulativeFederalTax(
            decimal totalGrossIncome,
            YearlyTaxData yearData,
            string filingStatus)
        {
            ArgumentNullException.ThrowIfNull(yearData);

            if (!yearData.FederalStandardDeductions.TryGetValue(filingStatus, out decimal stdDeduction))
            {
                throw new InvalidOperationException(
                    $"Missing Federal standard deduction for status '{filingStatus}' in tax year {yearData.TaxYear}.");
            }

            if (!yearData.FederalTaxBrackets.TryGetValue(filingStatus, out var brackets) || brackets.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Missing Federal tax brackets for status '{filingStatus}' in tax year {yearData.TaxYear}.");
            }

            decimal taxableIncome = Math.Max(0m, totalGrossIncome - stdDeduction);
            decimal totalTax = 0m;

            foreach (var b in brackets)
            {
                if (taxableIncome > b.IncomeMin)
                {
                    decimal upper = b.IncomeMax ?? decimal.MaxValue;
                    decimal taxableInBracket = Math.Min(taxableIncome, upper) - b.IncomeMin;
                    totalTax += taxableInBracket * b.TaxRate;
                }
            }

            return totalTax;
        }

        public static decimal CalculateCumulativeKentuckyTax(
            decimal totalRetirementAndPensionIncome,
            YearlyTaxData yearData,
            int qualifyingSpousesCount,
            bool applyKyStandardDeduction)
        {
            ArgumentNullException.ThrowIfNull(yearData);

            var ky = yearData.KentuckyRules;
            decimal totalExclusion = ky.PensionExclusion * Math.Max(1, qualifyingSpousesCount);
            decimal stdDed = applyKyStandardDeduction ? ky.StandardDeduction : 0m;

            decimal taxableKy = Math.Max(0m, totalRetirementAndPensionIncome - totalExclusion - stdDed);
            return taxableKy * ky.TaxRate;
        }

        /// <summary>
        /// Evaluates which Federal marginal tax bracket the total gross income falls into,
        /// how much room remains in that bracket, and the exact Roth conversion + Fed/KY tax
        /// amounts that will fill the current bracket to 100%.
        /// </summary>
        public static FederalBracketAndRothFillStatus EvaluateFederalBracketAndRothFill(
            decimal totalAnnualGrossIncome,
            YearlyTaxData yearData,
            string filingStatus,
            int kyQualifyingSpouses,
            bool applyKyStandardDeduction)
        {
            decimal stdDeduction = yearData.FederalStandardDeductions[filingStatus];
            var orderedBrackets = yearData.FederalTaxBrackets[filingStatus]
                .OrderBy(b => b.IncomeMin)
                .ToList();

            decimal currentMarginalRate;
            decimal bracketMin;
            decimal? bracketMax;
            decimal? remainingRoom;
            decimal? grossCeiling;
            decimal? nextMarginalRate;
            decimal taxableIncome = Math.Max(0m, totalAnnualGrossIncome - stdDeduction);

            if (totalAnnualGrossIncome < stdDeduction)
            {
                currentMarginalRate = 0m;
                bracketMin = 0m;
                bracketMax = 0m;
                remainingRoom = stdDeduction - totalAnnualGrossIncome;
                grossCeiling = stdDeduction;
                nextMarginalRate = orderedBrackets.Count > 0 ? orderedBrackets[0].TaxRate : null;
            }
            else
            {
                int activeIdx = 0;
                for (int i = 0; i < orderedBrackets.Count; i++)
                {
                    var b = orderedBrackets[i];
                    if (taxableIncome >= b.IncomeMin && (!b.IncomeMax.HasValue || taxableIncome <= b.IncomeMax.Value))
                    {
                        activeIdx = i;
                        if (taxableIncome > b.IncomeMin)
                            break;
                    }
                }

                var currentBracket = orderedBrackets[activeIdx];
                currentMarginalRate = currentBracket.TaxRate;
                bracketMin = currentBracket.IncomeMin;
                bracketMax = currentBracket.IncomeMax;
                nextMarginalRate = (activeIdx + 1 < orderedBrackets.Count)
                    ? orderedBrackets[activeIdx + 1].TaxRate
                    : null;

                remainingRoom = currentBracket.IncomeMax.HasValue
                    ? Math.Max(0m, currentBracket.IncomeMax.Value - taxableIncome)
                    : null;

                grossCeiling = currentBracket.IncomeMax.HasValue
                    ? stdDeduction + currentBracket.IncomeMax.Value
                    : null;
            }

            decimal rothNetIfGrossedUp = 0m;
            decimal rothFedTaxIfGrossedUp = 0m;
            decimal rothKyTaxIfGrossedUp = 0m;
            decimal rothPrincipalExternal = 0m;
            decimal rothFedTaxExternal = 0m;
            decimal rothKyTaxExternal = 0m;

            if (remainingRoom.HasValue && remainingRoom.Value > 0m)
            {
                decimal room = Math.Round(remainingRoom.Value, 2, MidpointRounding.AwayFromZero);
                decimal ceilingGrossIncome = totalAnnualGrossIncome + room;

                decimal currentFedTax = CalculateCumulativeFederalTax(totalAnnualGrossIncome, yearData, filingStatus);
                decimal ceilingFedTax = CalculateCumulativeFederalTax(ceilingGrossIncome, yearData, filingStatus);
                decimal incFedTaxForRoom = Math.Round(ceilingFedTax - currentFedTax, 2, MidpointRounding.AwayFromZero);

                decimal currentKyTax = CalculateCumulativeKentuckyTax(
                    totalAnnualGrossIncome, yearData, kyQualifyingSpouses, applyKyStandardDeduction);
                decimal ceilingKyTax = CalculateCumulativeKentuckyTax(
                    ceilingGrossIncome, yearData, kyQualifyingSpouses, applyKyStandardDeduction);
                decimal incKyTaxForRoom = Math.Round(ceilingKyTax - currentKyTax, 2, MidpointRounding.AwayFromZero);

                // Scenario A: Taxes on the Roth conversion are paid via an increased IRA withdrawal.
                rothFedTaxIfGrossedUp = incFedTaxForRoom;
                rothKyTaxIfGrossedUp = incKyTaxForRoom;
                rothNetIfGrossedUp = Math.Max(0m, room - rothFedTaxIfGrossedUp - rothKyTaxIfGrossedUp);

                // Scenario B: Taxes on the Roth conversion are paid from external cash.
                rothPrincipalExternal = room;
                rothFedTaxExternal = incFedTaxForRoom;
                rothKyTaxExternal = incKyTaxForRoom;
            }

            return new FederalBracketAndRothFillStatus(
                TotalAnnualGrossIncome: totalAnnualGrossIncome,
                FederalStandardDeduction: stdDeduction,
                TotalFederalTaxableIncome: taxableIncome,
                CurrentMarginalRate: currentMarginalRate,
                BracketTaxableMin: bracketMin,
                BracketTaxableMax: bracketMax,
                RemainingRoomInCurrentBracket: remainingRoom,
                GrossIncomeCeilingForCurrentBracket: grossCeiling,
                NextMarginalRate: nextMarginalRate,
                AdditionalRothNetIfTaxGrossedUp: rothNetIfGrossedUp,
                AdditionalRothFedTaxIfGrossedUp: rothFedTaxIfGrossedUp,
                AdditionalRothKyTaxIfGrossedUp: rothKyTaxIfGrossedUp,
                AdditionalRothPrincipalIfTaxPaidExternally: rothPrincipalExternal,
                AdditionalRothFedTaxIfPaidExternally: rothFedTaxExternal,
                AdditionalRothKyTaxIfPaidExternally: rothKyTaxExternal
            );
        }

        /// <summary>
        /// Computes automatic Federal and Kentucky tax withholdings on the gross pension payments
        /// and solves for the required gross IRA distribution stacked on top of the pension.
        /// </summary>
        public static GrossUpCalculationResult SolveGrossDistribution(
            YearlyTaxData yearData,
            DateTime plannedWithdrawalDate,
            string filingStatus,
            decimal priorDistributionsYtd,
            decimal targetNetDistribution,
            decimal monthlyGrossPension,
            int pensionStartMonth,
            decimal plannedRothConversion,
            bool includeRothConversionTaxInGrossUp,
            int kyQualifyingSpouses = 1,
            bool applyKyStdDeduction = true)
        {
            ArgumentNullException.ThrowIfNull(yearData);

            if (priorDistributionsYtd < 0m || targetNetDistribution < 0m || monthlyGrossPension < 0m || plannedRothConversion < 0m)
                throw new ArgumentOutOfRangeException("Income, conversion, and distribution amounts cannot be negative.");

            string irsQuarter = GetIrsEstimatedTaxQuarter(plannedWithdrawalDate);

            // Step 1: Compute Annualized Gross Pension & Automatic Pension Tax Withholding
            int pensionMonths = (monthlyGrossPension > 0m && pensionStartMonth is >= 1 and <= 12)
                ? (13 - pensionStartMonth)
                : 0;
            decimal annualPensionGross = monthlyGrossPension * pensionMonths;

            decimal fedTaxPrior = CalculateCumulativeFederalTax(priorDistributionsYtd, yearData, filingStatus);
            decimal kyTaxPrior = CalculateCumulativeKentuckyTax(priorDistributionsYtd, yearData, kyQualifyingSpouses, applyKyStdDeduction);

            decimal priorPlusPension = priorDistributionsYtd + annualPensionGross;
            decimal fedTaxPriorPlusPension = CalculateCumulativeFederalTax(priorPlusPension, yearData, filingStatus);
            decimal kyTaxPriorPlusPension = CalculateCumulativeKentuckyTax(priorPlusPension, yearData, kyQualifyingSpouses, applyKyStdDeduction);

            // Taxes automatically withheld directly from the gross pension payments
            decimal pensionFedWithheld = Math.Round(fedTaxPriorPlusPension - fedTaxPrior, 2, MidpointRounding.AwayFromZero);
            decimal pensionKyWithheld = Math.Round(kyTaxPriorPlusPension - kyTaxPrior, 2, MidpointRounding.AwayFromZero);
            decimal pensionTotalTaxWithheld = pensionFedWithheld + pensionKyWithheld;
            decimal annualPensionNet = Math.Max(0m, annualPensionGross - pensionTotalTaxWithheld);
            decimal monthlyPensionNet = pensionMonths > 0
                ? Math.Round(annualPensionNet / pensionMonths, 2, MidpointRounding.AwayFromZero)
                : 0m;

            // Step 2: Cumulative tax on (Prior YTD + Annual Pension + Planned Roth Conversion)
            decimal priorPlusPensionPlusRoth = priorPlusPension + plannedRothConversion;
            decimal fedTaxWithRoth = CalculateCumulativeFederalTax(priorPlusPensionPlusRoth, yearData, filingStatus);
            decimal kyTaxWithRoth = CalculateCumulativeKentuckyTax(priorPlusPensionPlusRoth, yearData, kyQualifyingSpouses, applyKyStdDeduction);

            decimal rothFedTax = fedTaxWithRoth - fedTaxPriorPlusPension;
            decimal rothKyTax = kyTaxWithRoth - kyTaxPriorPlusPension;

            // Step 3: Baseline tax already covered before the IRA distribution is taken.
            // Because Federal and KY taxes are automatically withheld from the gross pension payments,
            // fedTaxPriorPlusPension and kyTaxPriorPlusPension are always covered prior to the IRA withdrawal.
            decimal baselineFedTaxCovered = fedTaxPriorPlusPension + (includeRothConversionTaxInGrossUp ? 0m : rothFedTax);
            decimal baselineKyTaxCovered = kyTaxPriorPlusPension + (includeRothConversionTaxInGrossUp ? 0m : rothKyTax);

            bool needsGrossUpForRothTaxes = includeRothConversionTaxInGrossUp && plannedRothConversion > 0m;

            if (targetNetDistribution <= 0m && !needsGrossUpForRothTaxes)
            {
                var emptyBracket = EvaluateFederalBracketAndRothFill(
                    priorPlusPensionPlusRoth, yearData, filingStatus, kyQualifyingSpouses, applyKyStdDeduction);

                return new GrossUpCalculationResult(
                    TaxYear: yearData.TaxYear,
                    PlannedWithdrawalDate: plannedWithdrawalDate,
                    IrsEstimatedTaxQuarter: irsQuarter,
                    FilingStatus: filingStatus,
                    PriorDistributionsYtd: priorDistributionsYtd,
                    MonthlyPensionGross: monthlyGrossPension,
                    PensionStartMonth: pensionStartMonth,
                    PensionMonthsActive: pensionMonths,
                    AnnualizedPensionGross: annualPensionGross,
                    PensionFedTaxAutoWithheld: pensionFedWithheld,
                    PensionKyTaxAutoWithheld: pensionKyWithheld,
                    PensionTotalTaxAutoWithheld: pensionTotalTaxWithheld,
                    AnnualizedPensionNet: annualPensionNet,
                    MonthlyPensionNet: monthlyPensionNet,
                    PlannedRothConversion: plannedRothConversion,
                    TargetNetDistribution: 0m,
                    RequiredGrossDistribution: 0m,
                    TotalIraGrossActivity: plannedRothConversion,
                    DistributionBaseFedTaxWithheld: 0m,
                    ExtraRothFedTaxWithheld: 0m,
                    TotalDistributionFedTaxWithheld: 0m,
                    DistributionBaseKyTaxWithheld: 0m,
                    ExtraRothKyTaxWithheld: 0m,
                    TotalDistributionKyTaxWithheld: 0m,
                    TotalDistributionTaxesWithheld: 0m,
                    EffectiveFedWithholdingPct: 0m,
                    EffectiveKyWithholdingPct: 0m,
                    EffectiveTotalWithholdingPct: 0m,
                    BracketStatus: emptyBracket
                );
            }

            // Step 4: Binary Search for Required Gross IRA Withdrawal (stacked above Prior YTD + Pension + Roth)
            decimal low = targetNetDistribution;
            decimal high = (targetNetDistribution + plannedRothConversion + 50000m) * 2.5m;
            decimal gross = low;

            for (int i = 0; i < 100; i++)
            {
                gross = (low + high) / 2m;
                decimal totalAnnualTaxableGross = priorPlusPensionPlusRoth + gross;

                decimal distFedWithheld = CalculateCumulativeFederalTax(totalAnnualTaxableGross, yearData, filingStatus) - baselineFedTaxCovered;
                decimal distKyWithheld = CalculateCumulativeKentuckyTax(totalAnnualTaxableGross, yearData, kyQualifyingSpouses, applyKyStdDeduction) - baselineKyTaxCovered;

                decimal netCash = gross - distFedWithheld - distKyWithheld;
                decimal diff = netCash - targetNetDistribution;

                if (Math.Abs(diff) < 0.0001m)
                    break;

                if (netCash < targetNetDistribution)
                    low = gross;
                else
                    high = gross;
            }

            // Step 5: Round currency values to cents and reconcile any 1-cent rounding drift
            gross = Math.Round(gross, 2, MidpointRounding.AwayFromZero);
            decimal finalTotalIncome = priorPlusPensionPlusRoth + gross;

            decimal finalDistFedWithheld = Math.Round(
                CalculateCumulativeFederalTax(finalTotalIncome, yearData, filingStatus) - baselineFedTaxCovered,
                2, MidpointRounding.AwayFromZero);

            decimal finalDistKyWithheld = Math.Round(
                CalculateCumulativeKentuckyTax(finalTotalIncome, yearData, kyQualifyingSpouses, applyKyStdDeduction) - baselineKyTaxCovered,
                2, MidpointRounding.AwayFromZero);

            decimal actualNet = gross - finalDistFedWithheld - finalDistKyWithheld;
            if (actualNet != targetNetDistribution)
            {
                gross += (targetNetDistribution - actualNet);
                finalTotalIncome = priorPlusPensionPlusRoth + gross;
            }

            decimal extraRothFed = includeRothConversionTaxInGrossUp ? Math.Round(rothFedTax, 2, MidpointRounding.AwayFromZero) : 0m;
            decimal extraRothKy = includeRothConversionTaxInGrossUp ? Math.Round(rothKyTax, 2, MidpointRounding.AwayFromZero) : 0m;

            decimal baseDistFed = finalDistFedWithheld - extraRothFed;
            decimal baseDistKy = finalDistKyWithheld - extraRothKy;
            decimal totalDistTaxWithheld = finalDistFedWithheld + finalDistKyWithheld;

            // Step 6: Compute effective custodian withholding percentages on the gross IRA cash withdrawal
            decimal fedPct = gross > 0m ? Math.Round((finalDistFedWithheld / gross) * 100m, 2) : 0m;
            decimal kyPct = gross > 0m ? Math.Round((finalDistKyWithheld / gross) * 100m, 2) : 0m;
            decimal totalPct = gross > 0m ? Math.Round((totalDistTaxWithheld / gross) * 100m, 2) : 0m;

            // Step 7: Evaluate active Federal bracket and Bright Orange Roth conversion fill capacity
            var bracketStatus = EvaluateFederalBracketAndRothFill(
                finalTotalIncome, yearData, filingStatus, kyQualifyingSpouses, applyKyStdDeduction);

            return new GrossUpCalculationResult(
                TaxYear: yearData.TaxYear,
                PlannedWithdrawalDate: plannedWithdrawalDate,
                IrsEstimatedTaxQuarter: irsQuarter,
                FilingStatus: filingStatus,
                PriorDistributionsYtd: priorDistributionsYtd,
                MonthlyPensionGross: monthlyGrossPension,
                PensionStartMonth: pensionStartMonth,
                PensionMonthsActive: pensionMonths,
                AnnualizedPensionGross: annualPensionGross,
                PensionFedTaxAutoWithheld: pensionFedWithheld,
                PensionKyTaxAutoWithheld: pensionKyWithheld,
                PensionTotalTaxAutoWithheld: pensionTotalTaxWithheld,
                AnnualizedPensionNet: annualPensionNet,
                MonthlyPensionNet: monthlyPensionNet,
                PlannedRothConversion: plannedRothConversion,
                TargetNetDistribution: targetNetDistribution,
                RequiredGrossDistribution: gross,
                TotalIraGrossActivity: gross + plannedRothConversion,
                DistributionBaseFedTaxWithheld: baseDistFed,
                ExtraRothFedTaxWithheld: extraRothFed,
                TotalDistributionFedTaxWithheld: finalDistFedWithheld,
                DistributionBaseKyTaxWithheld: baseDistKy,
                ExtraRothKyTaxWithheld: extraRothKy,
                TotalDistributionKyTaxWithheld: finalDistKyWithheld,
                TotalDistributionTaxesWithheld: totalDistTaxWithheld,
                EffectiveFedWithholdingPct: fedPct,
                EffectiveKyWithholdingPct: kyPct,
                EffectiveTotalWithholdingPct: totalPct,
                BracketStatus: bracketStatus
            );
        }
    }

    /// <summary>
    /// Primary Windows Forms Dark-Mode UI featuring Royal Blue pension inputs/outputs,
    /// Bright Orange Roth conversion bracket-fill recommendations, and a wide 1140px layout
    /// engineered so six-figure ($100,000.00+) currency outputs never wrap.
    /// </summary>
    public sealed class MainForm : Form
    {
        private Dictionary<int, YearlyTaxData> _taxYears = new();
        private bool _isInitializing = true;
        private bool _isSyncingDateAndYear = false;
        private GrossUpCalculationResult? _lastResult;

        // High-Contrast Dark Mode Color Palette
        private static readonly Color BgDark = Color.FromArgb(24, 26, 31);
        private static readonly Color PanelDark = Color.FromArgb(34, 38, 46);
        private static readonly Color InputDark = Color.FromArgb(45, 50, 60);
        private static readonly Color TextBright = Color.FromArgb(240, 244, 248);
        private static readonly Color TextMuted = Color.FromArgb(160, 172, 188);
        private static readonly Color AccentCyan = Color.FromArgb(88, 166, 255);
        private static readonly Color AccentGreen = Color.FromArgb(63, 185, 80);
        private static readonly Color AccentPurple = Color.FromArgb(188, 140, 255);
        private static readonly Color AccentRoyalBlue = Color.RoyalBlue; // #4169E1 Royal Blue for all Pension items
        private static readonly Color AccentBrightOrange = Color.FromArgb(255, 140, 0); // #FF8C00 Bright Orange for Roth items
        private static readonly Color AccentRed = Color.FromArgb(248, 81, 73);

        // Input Controls
        private TextBox txtConnString = null!;
        private Label lblDbStatus = null!;
        private ComboBox cboYear = null!;
        private DateTimePicker dtpWithdrawalDate = null!;
        private ComboBox cboFilingStatus = null!;
        private NumericUpDown numPriorDist = null!;
        private NumericUpDown numTargetNet = null!;
        private NumericUpDown numMonthlyPension = null!;
        private ComboBox cboPensionStartMonth = null!;
        private NumericUpDown numRothConversion = null!;
        private Button btnFillBracketWithRoth = null!;
        private CheckBox chkWithholdRothTax = null!;
        private CheckBox chkKyStdDed = null!;
        private CheckBox chkTwoSpousesKy = null!;
        private Button btnCalculate = null!;

        // Output Display Labels
        private Label lblGrossRequired = null!;
        private Label lblWithdrawalDateQuarter = null!;
        // Royal Blue Pension Output Labels
        private Label lblAnnualPensionGrossSummary = null!;
        private Label lblPensionAutoWithheldSummary = null!;
        private Label lblPensionNetSummary = null!;
        // Bright Orange Roth & Distribution Output Labels
        private Label lblPlannedRothSummary = null!;
        private Label lblFedBaseTax = null!;
        private Label lblFedTotalWithheld = null!;
        private Label lblKyTotalWithheld = null!;
        private Label lblCombinedWithheld = null!;
        private Label lblNetVerified = null!;
        private Label lblCurrentFedBracket = null!;
        private Label lblRemainingBracketRoom = null!;
        private Label lblRothFillGrossedUp = null!;
        private Label lblRothFillExternalCash = null!;
        private Label lblRuleFooter = null!;

        public MainForm()
        {
            InitializeComponent();
            LoadTaxDataFromDatabase();
        }

        private void InitializeComponent()
        {
            Text = "Retirement Withdrawal, Pension & Roth Conversion Gross-Up Calculator (.NET 10)";
            // Wide 1140px dialog ensures zero wrapping even when all dollar amounts exceed $100,000.00
            Size = new Size(1140, 960);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = BgDark;
            ForeColor = TextBright;
            Font = new Font("Segoe UI", 9.75F);

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                // Ignore if no icon is embedded in executable
            }

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                ColumnCount = 2,
                RowCount = 15
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54F));

            // Row 0: SQL Connection Bar
            var connPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                Height = 34,
                Margin = new Padding(0, 0, 0, 4)
            };
            connPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            connPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            connPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            connPanel.Controls.Add(CreateLabel("SQL Connection:", false, TextMuted), 0, 0);
            txtConnString = new TextBox
            {
                Text = SqlTaxRepository.DefaultConnectionString,
                Dock = DockStyle.Fill,
                BackColor = InputDark,
                ForeColor = TextBright,
                BorderStyle = BorderStyle.FixedSingle
            };
            connPanel.Controls.Add(txtConnString, 1, 0);

            var btnReloadDb = new Button
            {
                Text = "Reload DB",
                AutoSize = true,
                BackColor = PanelDark,
                ForeColor = AccentCyan,
                FlatStyle = FlatStyle.Flat
            };
            btnReloadDb.Click += (_, _) => LoadTaxDataFromDatabase();
            connPanel.Controls.Add(btnReloadDb, 2, 0);

            root.Controls.Add(connPanel, 0, 0);
            root.SetColumnSpan(connPanel, 2);

            // Row 1: Database Connection Status Indicator
            lblDbStatus = new Label
            {
                Text = "Connecting to SQL Server...",
                AutoSize = true,
                ForeColor = AccentGreen,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
                Margin = new Padding(0, 0, 0, 6)
            };
            root.Controls.Add(lblDbStatus, 0, 1);
            root.SetColumnSpan(lblDbStatus, 2);

            // Row 2: Tax Year Dropdown
            root.Controls.Add(CreateLabel("Withdrawal Tax Year (from DB):"), 0, 2);
            cboYear = CreateComboBox();
            cboYear.SelectedIndexChanged += (_, _) => OnYearChanged();
            root.Controls.Add(cboYear, 1, 2);

            // Row 3: Planned Withdrawal Date Picker (Defaults to Jan 1 of TaxYear, or Today if in current year)
            root.Controls.Add(CreateLabel("Planned Withdrawal Date:"), 0, 3);
            dtpWithdrawalDate = new DateTimePicker
            {
                Dock = DockStyle.Fill,
                Format = DateTimePickerFormat.Long,
                CalendarMonthBackground = InputDark,
                CalendarForeColor = TextBright,
                CalendarTitleBackColor = PanelDark,
                CalendarTitleForeColor = AccentCyan
            };
            dtpWithdrawalDate.ValueChanged += (_, _) => OnWithdrawalDateChanged();
            root.Controls.Add(dtpWithdrawalDate, 1, 3);

            // Row 4: Tax Filing Status Dropdown
            root.Controls.Add(CreateLabel("Tax Filing Status:"), 0, 4);
            cboFilingStatus = CreateComboBox();
            cboFilingStatus.SelectedIndexChanged += (_, _) => RunCalculation();
            root.Controls.Add(cboFilingStatus, 1, 4);

            // Row 5: Prior YTD Distributions Input
            root.Controls.Add(CreateLabel("Prior Distributions Taken YTD ($):"), 0, 5);
            numPriorDist = CreateCurrencyInput(0.00m);
            numPriorDist.ValueChanged += (_, _) => RunCalculation();
            root.Controls.Add(numPriorDist, 1, 5);

            // Row 6: Target Net Distribution Input
            root.Controls.Add(CreateLabel("Target Net Distribution to Receive ($):"), 0, 6);
            numTargetNet = CreateCurrencyInput(100000.00m);
            numTargetNet.ValueChanged += (_, _) => RunCalculation();
            root.Controls.Add(numTargetNet, 1, 6);

            // Row 7: ROYAL BLUE - Estimated Monthly Gross Pension Input (Auto-Withholds Fed & KY Taxes)
            root.Controls.Add(
                CreateLabel("Gross Pension Per Month (Fed & KY Auto-Withheld) ($):", true, AccentRoyalBlue), 0, 7);
            numMonthlyPension = CreateCurrencyInput(0.00m, increment: 250m);
            numMonthlyPension.ForeColor = AccentRoyalBlue;
            numMonthlyPension.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold);
            numMonthlyPension.ValueChanged += (_, _) => RunCalculation();
            root.Controls.Add(numMonthlyPension, 1, 7);

            // Row 8: ROYAL BLUE - Month Pension Payments Begin Dropdown
            root.Controls.Add(
                CreateLabel("Month Gross Pension Payments Begin:", true, AccentRoyalBlue), 0, 8);
            cboPensionStartMonth = CreateComboBox();
            cboPensionStartMonth.ForeColor = AccentRoyalBlue;
            cboPensionStartMonth.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            string[] months =
            [
                "1 - January (12 payments)",
                "2 - February (11 payments)",
                "3 - March (10 payments)",
                "4 - April (9 payments)",
                "5 - May (8 payments)",
                "6 - June (7 payments)",
                "7 - July (6 payments)",
                "8 - August (5 payments)",
                "9 - September (4 payments)",
                "10 - October (3 payments)",
                "11 - November (2 payments)",
                "12 - December (1 payment)"
            ];
            cboPensionStartMonth.Items.AddRange(months);
            cboPensionStartMonth.SelectedIndex = 4; // Defaults to May (Month 5 -> 8 monthly payments)
            cboPensionStartMonth.SelectedIndexChanged += (_, _) => RunCalculation();
            root.Controls.Add(cboPensionStartMonth, 1, 8);

            // Row 9: BRIGHT ORANGE - Planned Roth Conversion Input + One-Click "Fill Bracket" Button
            root.Controls.Add(CreateLabel("Planned Roth IRA Conversion ($):", true, AccentBrightOrange), 0, 9);
            var rothInputPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Height = 32,
                Margin = new Padding(0)
            };
            rothInputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68F));
            rothInputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32F));

            numRothConversion = CreateCurrencyInput(0.00m, increment: 500m);
            numRothConversion.ForeColor = AccentBrightOrange;
            numRothConversion.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold);
            numRothConversion.ValueChanged += (_, _) => RunCalculation();
            rothInputPanel.Controls.Add(numRothConversion, 0, 0);

            btnFillBracketWithRoth = new Button
            {
                Text = "Fill Bracket",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(60, 38, 14),
                ForeColor = AccentBrightOrange,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.75F, FontStyle.Bold),
                Margin = new Padding(6, 1, 0, 1)
            };
            btnFillBracketWithRoth.FlatAppearance.BorderColor = AccentBrightOrange;
            btnFillBracketWithRoth.Click += (_, _) => ApplyMaxRothConversionToFillBracket();
            rothInputPanel.Controls.Add(btnFillBracketWithRoth, 1, 0);

            root.Controls.Add(rothInputPanel, 1, 9);

            // Row 10: Extra Withholding for Roth Conversion Checkbox
            chkWithholdRothTax = new CheckBox
            {
                Text = "Include extra withholding in IRA distribution to pay Federal & KY taxes on the Roth Conversion",
                Checked = true,
                AutoSize = true,
                ForeColor = AccentBrightOrange,
                Margin = new Padding(0, 4, 0, 2)
            };
            chkWithholdRothTax.CheckedChanged += (_, _) => RunCalculation();
            root.Controls.Add(chkWithholdRothTax, 0, 10);
            root.SetColumnSpan(chkWithholdRothTax, 2);

            // Row 11: KY Standard Deduction Checkbox
            chkKyStdDed = new CheckBox
            {
                Text = "Apply Kentucky Standard Deduction (Loaded from [dbo].[KentuckyTaxRates])",
                Checked = true,
                AutoSize = true,
                ForeColor = TextBright,
                Margin = new Padding(0, 2, 0, 2)
            };
            chkKyStdDed.CheckedChanged += (_, _) => RunCalculation();
            root.Controls.Add(chkKyStdDed, 0, 11);
            root.SetColumnSpan(chkKyStdDed, 2);

            // Row 12: KY Two-Spouse Exclusion Checkbox
            chkTwoSpousesKy = new CheckBox
            {
                Text = "Both spouses receive qualifying KY retirement distributions (2x Schedule P Exclusion)",
                Checked = false,
                AutoSize = true,
                ForeColor = TextBright,
                Margin = new Padding(0, 2, 0, 6)
            };
            chkTwoSpousesKy.CheckedChanged += (_, _) => RunCalculation();
            root.Controls.Add(chkTwoSpousesKy, 0, 12);
            root.SetColumnSpan(chkTwoSpousesKy, 2);

            // Row 13: Calculate & Write to Log File Button
            btnCalculate = new Button
            {
                Text = "Calculate Gross Distribution & Write to Log",
                Dock = DockStyle.Fill,
                Height = 38,
                BackColor = Color.FromArgb(31, 111, 235),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Margin = new Padding(0, 2, 0, 8)
            };
            btnCalculate.FlatAppearance.BorderSize = 0;
            btnCalculate.Click += (_, _) => RunCalculation(logToFile: true);
            root.Controls.Add(btnCalculate, 0, 13);
            root.SetColumnSpan(btnCalculate, 2);

            // Row 14: Results Summary GroupBox (38% Label / 62% Value split prevents any text wrapping)
            var grpResults = new GroupBox
            {
                Text = "Distribution Gross-Up, Royal Blue Pension Withholding & Bright Orange Roth Summary",
                Dock = DockStyle.Fill,
                BackColor = PanelDark,
                ForeColor = AccentCyan,
                Padding = new Padding(14),
                Font = new Font("Segoe UI", 9.75F, FontStyle.Bold)
            };

            var resGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 16,
                Font = new Font("Segoe UI", 9.25F, FontStyle.Regular)
            };
            resGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
            resGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62F));

            resGrid.Controls.Add(CreateLabel("Total Gross IRA Withdrawal Required:", true, TextBright), 0, 0);
            lblGrossRequired = CreateValueLabel(true, AccentCyan, 11.5F);
            resGrid.Controls.Add(lblGrossRequired, 1, 0);

            resGrid.Controls.Add(CreateLabel("Scheduled Date & IRS Tax Quarter:", false, TextMuted), 0, 1);
            lblWithdrawalDateQuarter = CreateValueLabel(false, TextMuted);
            resGrid.Controls.Add(lblWithdrawalDateQuarter, 1, 1);

            // ROYAL BLUE PENSION OUTPUT ROWS (Rows 2, 3, 4)
            resGrid.Controls.Add(CreateLabel("Annualized Gross Pension (Months Active):", true, AccentRoyalBlue), 0, 2);
            lblAnnualPensionGrossSummary = CreateValueLabel(true, AccentRoyalBlue);
            resGrid.Controls.Add(lblAnnualPensionGrossSummary, 1, 2);

            resGrid.Controls.Add(CreateLabel("Pension Taxes Auto-Withheld (Fed & KY):", true, AccentRoyalBlue), 0, 3);
            lblPensionAutoWithheldSummary = CreateValueLabel(true, AccentRoyalBlue);
            resGrid.Controls.Add(lblPensionAutoWithheldSummary, 1, 3);

            resGrid.Controls.Add(CreateLabel("Estimated Net Pension (Annual & Monthly):", true, AccentRoyalBlue), 0, 4);
            lblPensionNetSummary = CreateValueLabel(true, AccentRoyalBlue);
            resGrid.Controls.Add(lblPensionNetSummary, 1, 4);

            // ROTH & IRA DISTRIBUTION OUTPUT ROWS
            resGrid.Controls.Add(CreateLabel("Planned Roth Conversion (100% to Roth):", false, AccentBrightOrange), 0, 5);
            lblPlannedRothSummary = CreateValueLabel(false, AccentBrightOrange);
            resGrid.Controls.Add(lblPlannedRothSummary, 1, 5);

            resGrid.Controls.Add(CreateLabel("Federal & KY Tax on Living-Expense Withdrawal:"), 0, 6);
            lblFedBaseTax = CreateValueLabel();
            resGrid.Controls.Add(lblFedBaseTax, 1, 6);

            resGrid.Controls.Add(CreateLabel("IRA Federal Tax to Withhold (% of Gross):", true), 0, 7);
            lblFedTotalWithheld = CreateValueLabel(true);
            resGrid.Controls.Add(lblFedTotalWithheld, 1, 7);

            resGrid.Controls.Add(CreateLabel("IRA Kentucky Tax to Withhold (% of Gross):", true), 0, 8);
            lblKyTotalWithheld = CreateValueLabel(true);
            resGrid.Controls.Add(lblKyTotalWithheld, 1, 8);

            resGrid.Controls.Add(CreateLabel("Combined IRA Fed + KY Taxes Withheld:"), 0, 9);
            lblCombinedWithheld = CreateValueLabel();
            resGrid.Controls.Add(lblCombinedWithheld, 1, 9);

            resGrid.Controls.Add(CreateLabel("Net Living-Expense Cash Delivered to You:", true), 0, 10);
            lblNetVerified = CreateValueLabel(true, AccentGreen);
            resGrid.Controls.Add(lblNetVerified, 1, 10);

            // Federal Bracket Status Row
            resGrid.Controls.Add(CreateLabel("Active Federal Marginal Tax Bracket:", true, AccentPurple), 0, 11);
            lblCurrentFedBracket = CreateValueLabel(true, AccentPurple);
            resGrid.Controls.Add(lblCurrentFedBracket, 1, 11);

            // Bright Orange Roth Conversion Bracket-Fill Highlight Rows
            resGrid.Controls.Add(CreateLabel("Remaining Room to Fill Current Fed Bracket:", true, AccentBrightOrange), 0, 12);
            lblRemainingBracketRoom = CreateValueLabel(true, AccentBrightOrange, 10F);
            resGrid.Controls.Add(lblRemainingBracketRoom, 1, 12);

            resGrid.Controls.Add(CreateLabel("Max Add'l Roth to Fill Bracket (IRA Pays Tax):", true, AccentBrightOrange), 0, 13);
            lblRothFillGrossedUp = CreateValueLabel(true, AccentBrightOrange, 9.5F);
            resGrid.Controls.Add(lblRothFillGrossedUp, 1, 13);

            resGrid.Controls.Add(CreateLabel("Max Add'l Roth to Fill Bracket (Cash Pays Tax):", true, AccentBrightOrange), 0, 14);
            lblRothFillExternalCash = CreateValueLabel(true, AccentBrightOrange, 9.5F);
            resGrid.Controls.Add(lblRothFillExternalCash, 1, 14);

            lblRuleFooter = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
                AutoSize = true,
                Padding = new Padding(0, 6, 0, 0)
            };
            resGrid.Controls.Add(lblRuleFooter, 0, 15);
            resGrid.SetColumnSpan(lblRuleFooter, 2);

            grpResults.Controls.Add(resGrid);
            root.Controls.Add(grpResults, 0, 14);
            root.SetColumnSpan(grpResults, 2);

            Controls.Add(root);
        }

        private void ApplyMaxRothConversionToFillBracket()
        {
            if (_lastResult is null) return;

            var brk = _lastResult.BracketStatus;
            decimal additionalRothToAdd = chkWithholdRothTax.Checked
                ? brk.AdditionalRothNetIfTaxGrossedUp
                : brk.AdditionalRothPrincipalIfTaxPaidExternally;

            if (additionalRothToAdd <= 0m) return;

            decimal targetRothTotal = Math.Min(numRothConversion.Maximum, numRothConversion.Value + additionalRothToAdd);
            numRothConversion.Value = Math.Round(targetRothTotal, 2, MidpointRounding.AwayFromZero);
        }

        private void LoadTaxDataFromDatabase()
        {
            _isInitializing = true;
            try
            {
                var repo = new SqlTaxRepository(txtConnString.Text.Trim());
                _taxYears = repo.LoadAllTaxYears();

                if (_taxYears.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No intersecting TaxYears found across [FederalStandardDeductions], [FederalTaxRates], and [KentuckyTaxRates].");
                }

                int maxYear = _taxYears.Keys.Max();
                lblDbStatus.Text =
                    $"Connected to SQL Server: Loaded {_taxYears.Count} tax year(s) from Federal & KY tables. Defaulted to MAX year ({maxYear}).";
                lblDbStatus.ForeColor = AccentGreen;

                cboYear.Items.Clear();
                foreach (int yr in _taxYears.Keys.OrderByDescending(y => y))
                {
                    cboYear.Items.Add(yr);
                }

                cboYear.SelectedIndex = 0;
                SyncDatePickerToSelectedYear(maxYear);
                PopulateFilingStatusesForSelectedYear();
                SetCalculationControlsEnabled(true);

                Log.Information(
                    "Successfully loaded {Count} tax years from SQL database. Defaulted UI to Max TaxYear = {MaxYear}.",
                    _taxYears.Count, maxYear);
            }
            catch (SqlException sqlEx)
            {
                HandleDatabaseLoadFailure($"SQL Error #{sqlEx.Number}: {sqlEx.Message}");
            }
            catch (Exception ex)
            {
                HandleDatabaseLoadFailure($"Database Error: {ex.Message}");
            }
            finally
            {
                _isInitializing = false;
                if (_taxYears.Count > 0)
                {
                    RunCalculation();
                }
            }
        }

        /// <summary>
        /// Sets the withdrawal DateTimePicker to January 1 of the selected withdrawal year,
        /// or to the current system date (DateTime.Today) if the calculator is started during
        /// the tax year of withdrawal.
        /// </summary>
        private void SyncDatePickerToSelectedYear(int targetYear)
        {
            _isSyncingDateAndYear = true;
            try
            {
                DateTime targetDate = (DateTime.Today.Year == targetYear)
                    ? DateTime.Today
                    : new DateTime(targetYear, 1, 1);

                int minDbYear = _taxYears.Count > 0 ? _taxYears.Keys.Min() : targetYear;
                int maxDbYear = _taxYears.Count > 0 ? _taxYears.Keys.Max() : targetYear;

                dtpWithdrawalDate.MinDate = new DateTime(minDbYear, 1, 1);
                dtpWithdrawalDate.MaxDate = new DateTime(maxDbYear, 12, 31);
                dtpWithdrawalDate.Value = targetDate;
            }
            finally
            {
                _isSyncingDateAndYear = false;
            }
        }

        private void OnWithdrawalDateChanged()
        {
            if (_isInitializing || _isSyncingDateAndYear) return;

            int pickedYear = dtpWithdrawalDate.Value.Year;
            if (cboYear.SelectedItem is int currentTaxYear && pickedYear != currentTaxYear && _taxYears.ContainsKey(pickedYear))
            {
                _isSyncingDateAndYear = true;
                try
                {
                    cboYear.SelectedItem = pickedYear;
                    PopulateFilingStatusesForSelectedYear();
                }
                finally
                {
                    _isSyncingDateAndYear = false;
                }
            }

            RunCalculation();
        }

        private void OnYearChanged()
        {
            if (_isInitializing || _isSyncingDateAndYear) return;

            if (cboYear.SelectedItem is int selectedYear)
            {
                SyncDatePickerToSelectedYear(selectedYear);
            }
            PopulateFilingStatusesForSelectedYear();
            RunCalculation();
        }

        private void HandleDatabaseLoadFailure(string statusMessage)
        {
            _taxYears.Clear();
            cboYear.Items.Clear();
            cboFilingStatus.Items.Clear();
            SetCalculationControlsEnabled(false);

            lblDbStatus.Text = statusMessage;
            lblDbStatus.ForeColor = AccentRed;
            lblRuleFooter.Text = "Verify [FederalStandardDeductions], [FederalTaxRates], and [KentuckyTaxRates] in [Guyton-Klinger-Withdrawals].";
        }

        private void SetCalculationControlsEnabled(bool enabled)
        {
            cboYear.Enabled = enabled;
            dtpWithdrawalDate.Enabled = enabled;
            cboFilingStatus.Enabled = enabled;
            numPriorDist.Enabled = enabled;
            numTargetNet.Enabled = enabled;
            numMonthlyPension.Enabled = enabled;
            cboPensionStartMonth.Enabled = enabled;
            numRothConversion.Enabled = enabled;
            btnFillBracketWithRoth.Enabled = enabled;
            chkWithholdRothTax.Enabled = enabled;
            chkKyStdDed.Enabled = enabled;
            chkTwoSpousesKy.Enabled = enabled;
            btnCalculate.Enabled = enabled;
        }

        private void PopulateFilingStatusesForSelectedYear()
        {
            if (cboYear.SelectedItem is not int selectedYear || !_taxYears.TryGetValue(selectedYear, out var yearData))
                return;

            string? previousSelection = cboFilingStatus.SelectedItem?.ToString();
            cboFilingStatus.Items.Clear();

            foreach (string status in yearData.FederalStandardDeductions.Keys)
            {
                cboFilingStatus.Items.Add(status);
            }

            int idx = -1;
            if (!string.IsNullOrEmpty(previousSelection))
                idx = cboFilingStatus.Items.IndexOf(previousSelection);

            if (idx < 0)
            {
                for (int i = 0; i < cboFilingStatus.Items.Count; i++)
                {
                    string s = cboFilingStatus.Items[i]?.ToString() ?? "";
                    if (s.Contains("Married", StringComparison.OrdinalIgnoreCase) ||
                        s.Contains("Joint", StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
            }

            cboFilingStatus.SelectedIndex = idx >= 0 ? idx : (cboFilingStatus.Items.Count > 0 ? 0 : -1);
        }

        private void RunCalculation(bool logToFile = false)
        {
            if (_isInitializing || cboYear.SelectedItem is not int year || cboFilingStatus.SelectedItem is null)
                return;

            try
            {
                string status = cboFilingStatus.SelectedItem.ToString()!;
                var yearData = _taxYears[year];
                var kyRules = yearData.KentuckyRules;

                bool isMarried = status.Contains("Married", StringComparison.OrdinalIgnoreCase) ||
                                 status.Contains("Joint", StringComparison.OrdinalIgnoreCase);

                if (!isMarried)
                {
                    chkTwoSpousesKy.Checked = false;
                    chkTwoSpousesKy.Enabled = false;
                }
                else
                {
                    chkTwoSpousesKy.Enabled = true;
                }

                chkKyStdDed.Text = $"Apply Kentucky Standard Deduction ({kyRules.StandardDeduction:C0} in {year})";
                chkTwoSpousesKy.Text =
                    $"Both spouses receive qualifying KY retirement distributions (2x {kyRules.PensionExclusion:C0} Schedule P Exclusion)";

                int kySpouses = (isMarried && chkTwoSpousesKy.Checked) ? 2 : 1;
                int pensionStartMonth = cboPensionStartMonth.SelectedIndex + 1;

                var res = TaxCalculatorEngine.SolveGrossDistribution(
                    yearData: yearData,
                    plannedWithdrawalDate: dtpWithdrawalDate.Value.Date,
                    filingStatus: status,
                    priorDistributionsYtd: numPriorDist.Value,
                    targetNetDistribution: numTargetNet.Value,
                    monthlyGrossPension: numMonthlyPension.Value,
                    pensionStartMonth: pensionStartMonth,
                    plannedRothConversion: numRothConversion.Value,
                    includeRothConversionTaxInGrossUp: chkWithholdRothTax.Checked,
                    kyQualifyingSpouses: kySpouses,
                    applyKyStdDeduction: chkKyStdDed.Checked
                );

                _lastResult = res;

                // Populate Core IRA Distribution Labels
                lblGrossRequired.Text = $"{res.RequiredGrossDistribution:C2}";
                lblWithdrawalDateQuarter.Text =
                    $"{res.PlannedWithdrawalDate:yyyy-MM-dd}  [{res.IrsEstimatedTaxQuarter}]";

                // Populate Royal Blue Self-Withholding Pension Labels
                lblAnnualPensionGrossSummary.Text =
                    $"{res.AnnualizedPensionGross:C2}  ({res.PensionMonthsActive} mos @ {res.MonthlyPensionGross:C2}/mo)";
                lblPensionAutoWithheldSummary.Text =
                    $"Fed: {res.PensionFedTaxAutoWithheld:C2}  |  KY: {res.PensionKyTaxAutoWithheld:C2}  (Total: {res.PensionTotalTaxAutoWithheld:C2})";
                lblPensionNetSummary.Text =
                    $"{res.AnnualizedPensionNet:C2}/yr  ({res.MonthlyPensionNet:C2}/mo net)";

                // Populate Roth & IRA Distribution Withholding Labels
                lblPlannedRothSummary.Text =
                    $"{res.PlannedRothConversion:C2}  (IRA Extra Withheld -> Fed: {res.ExtraRothFedTaxWithheld:C2} | KY: {res.ExtraRothKyTaxWithheld:C2})";
                lblFedBaseTax.Text =
                    $"Fed: {res.DistributionBaseFedTaxWithheld:C2}  |  KY: {res.DistributionBaseKyTaxWithheld:C2}";

                lblFedTotalWithheld.Text =
                    $"{res.TotalDistributionFedTaxWithheld:C2}  ({res.EffectiveFedWithholdingPct:F2}%)";
                lblKyTotalWithheld.Text =
                    $"{res.TotalDistributionKyTaxWithheld:C2}  ({res.EffectiveKyWithholdingPct:F2}%)";
                lblCombinedWithheld.Text =
                    $"{res.TotalDistributionTaxesWithheld:C2}  ({res.EffectiveTotalWithholdingPct:F2}%)";
                lblNetVerified.Text = $"{res.TargetNetDistribution:C2}";

                // Populate Active Federal Bracket & Bright Orange Roth Conversion Fill Rows
                var brk = res.BracketStatus;
                if (brk.CurrentMarginalRate == 0m)
                {
                    lblCurrentFedBracket.Text =
                        $"0.0% (Within {brk.FederalStandardDeduction:C0} Std Deduction)";
                }
                else if (brk.BracketTaxableMax.HasValue)
                {
                    lblCurrentFedBracket.Text =
                        $"{brk.CurrentMarginalRate:P1} Bracket (Taxable: {brk.TotalFederalTaxableIncome:C2} / {brk.BracketTaxableMax.Value:C0})";
                }
                else
                {
                    lblCurrentFedBracket.Text =
                        $"{brk.CurrentMarginalRate:P1} Top Bracket (Taxable: {brk.TotalFederalTaxableIncome:C2})";
                }

                if (brk.RemainingRoomInCurrentBracket.HasValue)
                {
                    string nextRateStr = brk.NextMarginalRate.HasValue ? $"before {brk.NextMarginalRate.Value:P0}" : "to bracket cap";
                    lblRemainingBracketRoom.Text =
                        $"{brk.RemainingRoomInCurrentBracket.Value:C2}  ({nextRateStr} | Gross Cap: {brk.GrossIncomeCeilingForCurrentBracket:C0})";

                    decimal grossedUpTotalFill = brk.AdditionalRothNetIfTaxGrossedUp
                        + brk.AdditionalRothFedTaxIfGrossedUp
                        + brk.AdditionalRothKyTaxIfGrossedUp;

                    lblRothFillGrossedUp.Text =
                        $"Convert {brk.AdditionalRothNetIfTaxGrossedUp:C2} + Fed {brk.AdditionalRothFedTaxIfGrossedUp:C2} + KY {brk.AdditionalRothKyTaxIfGrossedUp:C2} = {grossedUpTotalFill:C2}";

                    decimal externalTaxTotal = brk.AdditionalRothFedTaxIfPaidExternally + brk.AdditionalRothKyTaxIfPaidExternally;
                    lblRothFillExternalCash.Text =
                        $"Convert {brk.AdditionalRothPrincipalIfTaxPaidExternally:C2} (Pay {brk.AdditionalRothFedTaxIfPaidExternally:C2} Fed + {brk.AdditionalRothKyTaxIfPaidExternally:C2} KY = {externalTaxTotal:C2} cash)";
                }
                else
                {
                    lblRemainingBracketRoom.Text = "Uncapped (Top Marginal Federal Bracket)";
                    lblRothFillGrossedUp.Text = "N/A (Top 37% Bracket has no upper ceiling)";
                    lblRothFillExternalCash.Text = "N/A (Top 37% Bracket has no upper ceiling)";
                }

                decimal fedStd = yearData.FederalStandardDeductions[status];
                decimal kyExcl = kyRules.PensionExclusion * kySpouses;
                lblRuleFooter.Text =
                    $"DB Year {year} [{status}] | Total Gross Income: {brk.TotalAnnualGrossIncome:C2} | Fed Std Ded: {fedStd:C0} | KY Excl: {kyExcl:C0} | KY Rate: {kyRules.TaxRate:P2}";
                lblRuleFooter.ForeColor = TextMuted;

                if (logToFile)
                {
                    Log.Information(
                        "GrossUp Calc | Date: {Date:yyyy-MM-dd} ({Quarter}) | Year: {Year} | Status: {Status} | PriorYTD: {Prior:C2} | AnnualPensionGross: {AnnPen:C2} (PenFedWithheld: {PenFed:C2}, PenKyWithheld: {PenKy:C2}, AnnPenNet: {AnnPenNet:C2}) | PlannedRoth: {Roth:C2} | TargetNet: {Net:C2} | GrossWithdrawal: {Gross:C2} | IraFedWithheld: {Fed:C2} ({FedPct:F2}%) | IraKyWithheld: {Ky:C2} ({KyPct:F2}%) | ActiveBracket: {BrkRate:P1} | BracketRoomLeft: {Room:C2} | AddlRothToFill(GrossedUp): {RothFillNet:C2}",
                        res.PlannedWithdrawalDate, res.IrsEstimatedTaxQuarter,
                        res.TaxYear, res.FilingStatus, res.PriorDistributionsYtd,
                        res.AnnualizedPensionGross, res.PensionFedTaxAutoWithheld, res.PensionKyTaxAutoWithheld, res.AnnualizedPensionNet,
                        res.PlannedRothConversion, res.TargetNetDistribution, res.RequiredGrossDistribution,
                        res.TotalDistributionFedTaxWithheld, res.EffectiveFedWithholdingPct,
                        res.TotalDistributionKyTaxWithheld, res.EffectiveKyWithholdingPct,
                        brk.CurrentMarginalRate, brk.RemainingRoomInCurrentBracket ?? 0m,
                        brk.AdditionalRothNetIfTaxGrossedUp);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Calculation error for TaxYear {Year} and FilingStatus {Status}.", cboYear.SelectedItem, cboFilingStatus.SelectedItem);
                lblRuleFooter.Text = $"Calculation Error: {ex.Message}";
                lblRuleFooter.ForeColor = AccentRed;
            }
        }

        private static Label CreateLabel(string text, bool bold = false, Color? color = null) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = color ?? TextBright,
            Padding = new Padding(0, 4, 0, 4),
            Font = new Font("Segoe UI", 9.25F, bold ? FontStyle.Bold : FontStyle.Regular)
        };

        private static Label CreateValueLabel(bool bold = false, Color? color = null, float fontSize = 9.5F) => new()
        {
            Text = "$0.00",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            ForeColor = color ?? TextBright,
            Padding = new Padding(0, 4, 0, 4),
            Font = new Font("Segoe UI", fontSize, bold ? FontStyle.Bold : FontStyle.Regular)
        };

        private static ComboBox CreateComboBox() => new()
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = InputDark,
            ForeColor = TextBright,
            FlatStyle = FlatStyle.Flat
        };

        private static NumericUpDown CreateCurrencyInput(decimal defaultVal, decimal increment = 1000m) => new()
        {
            Dock = DockStyle.Fill,
            Maximum = 10000000m,
            DecimalPlaces = 2,
            ThousandsSeparator = true,
            Increment = increment,
            Value = defaultVal,
            BackColor = InputDark,
            ForeColor = TextBright
        };
    }

    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);

            // Configure Serilog exclusively for rolling file logging
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    path: Path.Combine(logDir, "tax-grossup-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnGuiThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnobservedDomainException;

            try
            {
                Log.Information("Starting RetirementGrossUpCalculator v1.4.0 (.NET 10)...");
                ApplicationConfiguration.Initialize();
                Application.Run(new MainForm());
                Log.Information("RetirementGrossUpCalculator exited cleanly.");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Fatal unhandled exception in Main().");
                MessageBox.Show(
                    $"A fatal error occurred and has been written to the log file:\n\n{ex.Message}",
                    "Fatal Application Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static void OnGuiThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Log.Error(e.Exception, "Unhandled Windows Forms UI thread exception.");
            MessageBox.Show(
                $"An unexpected error occurred and was logged to disk:\n\n{e.Exception.Message}",
                "Application Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private static void OnUnobservedDomainException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "Unhandled AppDomain exception (IsTerminating: {IsTerminating}).", e.IsTerminating);
            }
            else
            {
                Log.Fatal("Unhandled non-Exception AppDomain error: {ErrorObject}", e.ExceptionObject);
            }
            Log.CloseAndFlush();
        }
    }
}