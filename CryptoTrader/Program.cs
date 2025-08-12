using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot.Socket;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Transforms;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Telegram.Bot;

public class Program
{
    public class BotConfig
    {
        public string ApiKey { get; set; } = "YOUR_BINANCE_API_KEY";
        public string ApiSecret { get; set; } = "YOUR_BINANCE_API_SECRET";
        public string TelegramToken { get; set; } = "6299377057:AAHaNlY93hdrdQVanTPgmMibgQt41UDidRA";
        public string TelegramChatId { get; set; } = "1314937104";
        public string Symbol { get; set; } = "BTCUSDT";
        public decimal RiskPerTrade { get; set; } = 0.02m;
        public decimal MaxDailyLossPercent { get; set; } = 0.10m;
        public decimal AtrMultiplierSL { get; set; } = 1.5m;
        public decimal AtrMultiplierTP { get; set; } = 3.0m;
        public decimal MinAtrPercent { get; set; } = 0.01m;
        public decimal MinOrderSize { get; set; } = 0.0001m;
        public decimal MaxPositionSizePercent { get; set; } = 0.1m;
        public int[] FastMAPeriodRange { get; set; } = new[] { 5, 50 };
        public int[] SlowMAPeriodRange { get; set; } = new[] { 15, 100 };
        public int[] RSIPeriodRange { get; set; } = new[] { 10, 30 };
        public double[] OverboughtLevelRange { get; set; } = new[] { 60.0, 80.0 };
        public double[] OversoldLevelRange { get; set; } = new[] { 20.0, 40.0 };
        public decimal MinVolumeUSDT { get; set; } = 500000m;
        public decimal VolumeChangeThreshold { get; set; } = 0.3m;
        public decimal VolatilityThreshold { get; set; } = 0.005m;
        public int VolatilityPeriod { get; set; } = 14;
        public KlineInterval PrimaryTimeframe { get; set; } = KlineInterval.FifteenMinutes;
        public KlineInterval HigherTimeframe { get; set; } = KlineInterval.OneHour;
        public KlineInterval LowerTimeframe { get; set; } = KlineInterval.FiveMinutes;
        public int FastMAPeriod { get; set; } = 8;
        public int SlowMAPeriod { get; set; } = 34;
        public int RSIPeriod { get; set; } = 14;
        public double OverboughtLevel { get; set; } = 70.0;
        public double OversoldLevel { get; set; } = 30.0;
        public int BbPeriod { get; set; } = 20;
        public double BbStdDev { get; set; } = 2.0;
        public int FastEmaPeriod { get; set; } = 8;
        public int SlowEmaPeriod { get; set; } = 34;
        public int SignalPeriod { get; set; } = 9;
        public int AdxPeriod { get; set; } = 14;
        public double AdxThreshold { get; set; } = 25.0;
        public int MlLookbackPeriod { get; set; } = 100;
        public int MlPredictionHorizon { get; set; } = 10;
        public double MlConfidenceThreshold { get; set; } = 0.7;
        public bool UseMachineLearning { get; set; } = true;
        public int MlTrainingIntervalHours { get; set; } = 24;
        public int MlWindowSize { get; set; } = 200;
        public double MlPositiveClassWeight { get; set; } = 0.7;
        public int MlMinTrainingSamples { get; set; } = 1000;
        public int OptimizationGenerations { get; set; } = 10;
        public int OptimizationPopulationSize { get; set; } = 50;
        public int CheckIntervalMinutes { get; set; } = 5;
        public double OptimizationMinWinRate { get; set; } = 0.40;
        public double OptimizationMinProfitRatio { get; set; } = 0.70;
        public double OptimizationSharpeWeight { get; set; } = 0.4;
        public double OptimizationProfitWeight { get; set; } = 0.3;
        public double OptimizationWinRateWeight { get; set; } = 0.2;
        public double OptimizationDrawdownWeight { get; set; } = 0.1;
        public bool BacktestMode { get; set; } = true;
        public bool OptimizeMode { get; set; } = true;
        public bool LiveTradeMode { get; set; } = false;
        public DateTime BacktestStartDate { get; set; } = new DateTime(2025, 1, 1);
        public DateTime BacktestEndDate { get; set; } = DateTime.Now;
        public KlineInterval BacktestInterval { get; set; } = KlineInterval.FifteenMinutes;
        public decimal InitialBalance { get; set; } = 1000m;
        public decimal CommissionPercent { get; set; } = 0.04m;
        public decimal PartialTakeProfitLevel { get; set; } = 1.5m;
        public decimal PartialTakeProfitFactor { get; set; } = 0.5m;
        public int SupertrendPeriod { get; set; } = 10;
        public decimal SupertrendMultiplier { get; set; } = 3.0m;
        public decimal VolumeProfilePriceStep { get; set; } = 100m;
        public decimal OptimalTradeStartHour { get; set; } = 14;
        public decimal OptimalTradeEndHour { get; set; } = 22;
        public string FearGreedIndexUrl { get; set; } = "https://api.alternative.me/fng/";
        public bool EnableNightFilter { get; set; } = true;
    }

    public record TradingParams(
        int FastMAPeriod,
        int SlowMAPeriod,
        int RSIPeriod,
        double OverboughtLevel,
        double OversoldLevel,
        int BbPeriod,
        double BbStdDev)
    {
        public override string ToString() =>
            $"FastMA={FastMAPeriod}, SlowMA={SlowMAPeriod}, RSI={RSIPeriod}, " +
            $"OB={OverboughtLevel:F1}, OS={OversoldLevel:F1}, " +
            $"BB(P={BbPeriod},SD={BbStdDev:F1})";
    }

    public record TradeRecord(
        DateTime Timestamp,
        string Type,
        decimal Quantity,
        decimal EntryPrice,
        decimal ExitPrice,
        decimal StopLossPrice,
        decimal TakeProfitPrice,
        decimal PnL,
        decimal Commission,
        string Notes = "")
    {
        public bool IsClosed => ExitPrice != 0;
    }

    public class EnhancedMarketData
    {
        [LoadColumn(0)] public float Open { get; set; }
        [LoadColumn(1)] public float High { get; set; }
        [LoadColumn(2)] public float Low { get; set; }
        [LoadColumn(3)] public float Close { get; set; }
        [LoadColumn(4)] public float Volume { get; set; }
        [LoadColumn(5)] public float SMI { get; set; }
        [LoadColumn(6)] public float SMISignal { get; set; }
        [LoadColumn(7)] public float VWMA { get; set; }
        [LoadColumn(8)] public float WRSI { get; set; }
        [LoadColumn(9)] public float ADXVMA { get; set; }
        [LoadColumn(10)] public float BBUpper { get; set; }
        [LoadColumn(11)] public float BBLower { get; set; }
        [LoadColumn(12)] public float ATR { get; set; }
        [LoadColumn(13)] public float VolumeChange { get; set; }
        [LoadColumn(14)] public float HigherTrend { get; set; }
        [LoadColumn(15)] public float MarketSentiment { get; set; }
        [LoadColumn(16)] public float ADX { get; set; }
        [LoadColumn(17)] public float FearGreedIndex { get; set; }
        [LoadColumn(18)] public float SP500Correlation { get; set; }
        [LoadColumn(19)] public float FundingRate { get; set; }
        [LoadColumn(20)] public float OBV { get; set; }
        [LoadColumn(21)] public float VWAP { get; set; }
        [ColumnName("Label")]
        public bool Target { get; set; }
    }

    public class MarketPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool PredictedLabel { get; set; }

        [ColumnName("Score")]
        public float Score { get; set; }

        public bool ConfirmedPrediction => PredictedLabel && Score >= (float)config.MlConfidenceThreshold;
    }

    private static BotConfig config = new BotConfig();
    private static ILogger logger;
    private static decimal dailyPnL = 0;
    private static DateTime lastTradeDate = DateTime.MinValue;
    private static MLContext mlContext = new MLContext();
    private static ITransformer mlModel;
    private static DateTime lastModelTrainingTime = DateTime.MinValue;
    private static List<TradeRecord> tradeHistory = new List<TradeRecord>();
    private static Dictionary<DateTime, decimal> dailyBalances = new Dictionary<DateTime, decimal>();
    private static HttpClient httpClient = new HttpClient();

    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFile("logs/bot_{Date}.log");
        });

        logger = loggerFactory.CreateLogger("CryptoBot");
        var telegramBot = new TelegramBotClient(config.TelegramToken);

        try
        {
            var binanceClient = new BinanceRestClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(config.ApiKey, config.ApiSecret);
            });

            if (config.BacktestMode)
            {
                var defaultParams = new TradingParams(
                    config.FastMAPeriod,
                    config.SlowMAPeriod,
                    config.RSIPeriod,
                    config.OverboughtLevel,
                    config.OversoldLevel,
                    config.BbPeriod,
                    config.BbStdDev);

                await RunBacktestUniversal(binanceClient, telegramBot, "с параметрами по умолчанию", defaultParams);

                if (config.UseMachineLearning)
                {
                    await RunBacktestUniversal(binanceClient, telegramBot, "с интеграцией ML", defaultParams, true);
                }
            }

            if (config.OptimizeMode)
            {
                await OptimizeParameters(binanceClient, telegramBot);
            }

            if (config.BacktestMode)
            {
                var optimizedParams = new TradingParams(
                    config.FastMAPeriod,
                    config.SlowMAPeriod,
                    config.RSIPeriod,
                    config.OverboughtLevel,
                    config.OversoldLevel,
                    config.BbPeriod,
                    config.BbStdDev);

                await RunBacktestUniversal(binanceClient, telegramBot, "с оптимизированными параметрами", optimizedParams);
            }

            if (config.LiveTradeMode)
            {
                logger.LogInformation("Бот запущен в реальном режиме. Мониторинг рынка...");
                await TrainInitialModel(binanceClient);
                await RunLiveTrading(binanceClient, telegramBot);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Критическая ошибка в главном методе");
            await telegramBot.SendMessage(config.TelegramChatId, $"❌ Критическая ошибка: {ex.Message}");
        }
    }

    // Новые методы расчета индикаторов
    public static (double smi, double signal) CalculateSMI(double[] closes, int period, int smooth)
    {
        if (closes.Length < period) return (0, 0);

        double[] smiValues = new double[closes.Length];
        double[] ema1 = new double[closes.Length];
        double[] ema2 = new double[closes.Length];

        for (int i = period - 1; i < closes.Length; i++)
        {
            double max = closes.Skip(i - period + 1).Take(period).Max();
            double min = closes.Skip(i - period + 1).Take(period).Min();
            double range = max - min;

            if (range == 0)
            {
                smiValues[i] = 0;
                continue;
            }

            double diff = closes[i] - (max + min) / 2;
            double value = 200 * (diff / range);

            // Первое сглаживание
            if (i == period - 1)
            {
                ema1[i] = value;
            }
            else
            {
                ema1[i] = (value - ema1[i - 1]) * (2.0 / (smooth + 1)) + ema1[i - 1];
            }

            // Второе сглаживание
            if (i == period - 1)
            {
                ema2[i] = ema1[i];
            }
            else
            {
                ema2[i] = (ema1[i] - ema2[i - 1]) * (2.0 / (smooth + 1)) + ema2[i - 1];
            }

            smiValues[i] = ema2[i];
        }

        double smi = smiValues.LastOrDefault();
        double signal = smiValues.Length > smooth ?
            CalculateEma(smiValues.Skip(smiValues.Length - smooth).ToArray(), smooth) : smi;

        return (smi, signal);
    }

    public static double CalculateVWMA(List<IBinanceKline> klines, int period)
    {
        if (klines.Count < period) return 0;

        var relevant = klines.TakeLast(period);
        double sumPV = relevant.Sum(k => (double)k.ClosePrice * (double)k.Volume);
        double sumV = relevant.Sum(k => (double)k.Volume);

        return sumV > 0 ? sumPV / sumV : 0;
    }

    public static double CalculateWRSI(double[] closes, int period)
    {
        if (closes.Length <= period) return 50;

        double avgGain = 0;
        double avgLoss = 0;

        // Первые period баров
        for (int i = 1; i <= period; i++)
        {
            double change = closes[i] - closes[i - 1];
            if (change > 0) avgGain += change;
            else avgLoss -= change;
        }

        avgGain /= period;
        avgLoss /= period;

        // Последующие бары
        for (int i = period + 1; i < closes.Length; i++)
        {
            double change = closes[i] - closes[i - 1];
            if (change > 0)
            {
                avgGain = (avgGain * (period - 1) + change) / period;
                avgLoss = (avgLoss * (period - 1)) / period;
            }
            else
            {
                avgGain = (avgGain * (period - 1)) / period;
                avgLoss = (avgLoss * (period - 1) - change) / period;
            }
        }

        if (avgLoss == 0) return 100;
        double rs = avgGain / avgLoss;
        return 100 - (100 / (1 + rs));
    }

    public static double CalculateADXVMA(double[] closes, List<IBinanceKline> klines, int adxPeriod, int vwmaPeriod)
    {
        double adx = CalculateADX(klines, adxPeriod);
        double vwma = CalculateVWMA(klines, vwmaPeriod);
        return adx > config.AdxThreshold ? vwma : closes.Average();
    }

    public static double CalculateOBV(List<IBinanceKline> klines)
    {
        if (klines.Count < 2) return 0;

        double obv = 0;
        for (int i = 1; i < klines.Count; i++)
        {
            if (klines[i].ClosePrice > klines[i - 1].ClosePrice)
                obv += (double)klines[i].Volume;
            else if (klines[i].ClosePrice < klines[i - 1].ClosePrice)
                obv -= (double)klines[i].Volume;
        }
        return obv;
    }

    public static double CalculateVWAP(List<IBinanceKline> klines)
    {
        if (!klines.Any()) return 0;

        double cumulativePV = 0;
        double cumulativeVolume = 0;

        foreach (var k in klines)
        {
            double typicalPrice = (double)(k.HighPrice + k.LowPrice + k.ClosePrice) / 3;
            cumulativePV += typicalPrice * (double)k.Volume;
            cumulativeVolume += (double)k.Volume;
        }

        return cumulativeVolume > 0 ? cumulativePV / cumulativeVolume : 0;
    }

    // Обновленный метод подготовки данных для ML
    private static async Task<List<EnhancedMarketData>> PrepareEnhancedMLDataAsync(List<IBinanceKline> klines)
    {
        var mlData = new List<EnhancedMarketData>();
        if (klines == null || klines.Count < config.MlLookbackPeriod + config.MlPredictionHorizon)
        {
            logger.LogWarning($"Недостаточно данных для ML. Нужно: {config.MlLookbackPeriod + config.MlPredictionHorizon}, есть: {klines.Count}");
            return mlData;
        }

        int positiveCount = 0;
        int negativeCount = 0;

        var higherTimeframeKlines = AggregateKlinesToHigherTimeframe(klines, config.HigherTimeframe);

        for (int i = config.MlLookbackPeriod; i < klines.Count - config.MlPredictionHorizon; i++)
        {
            int predictionPoint = i;
            var currentWindow = klines.Take(predictionPoint).Skip(predictionPoint - config.MlLookbackPeriod).ToList();
            var futurePrices = klines.Skip(predictionPoint).Take(config.MlPredictionHorizon).Select(k => (double)k.ClosePrice).ToArray();

            if (currentWindow.Count < config.MlLookbackPeriod || futurePrices.Length < config.MlPredictionHorizon)
                continue;

            var closes = currentWindow.Select(k => (double)k.ClosePrice).ToArray();
            var volumes = currentWindow.Select(k => (double)k.Volume).ToArray();

            var (smi, smiSignal) = CalculateSMI(closes, 14, 3);
            var vwma = CalculateVWMA(currentWindow, 20);
            var wrsi = CalculateWRSI(closes, config.RSIPeriod);
            var adxvma = CalculateADXVMA(closes, currentWindow, config.AdxPeriod, 20);
            var (upperBB, _, lowerBB) = CalculateBollingerBands(closes, config.BbPeriod, config.BbStdDev);
            var atr = (float)CalculateATR(currentWindow, 14);
            var volumeChange = (float)(currentWindow.Last().Volume / currentWindow.Average(k => k.Volume) - 1);
            var adx = (float)CalculateADX(currentWindow, config.AdxPeriod);
            var obv = (float)CalculateOBV(currentWindow);
            var vwap = (float)CalculateVWAP(currentWindow);

            var higherCloses = higherTimeframeKlines
                .Where(k => k.OpenTime <= currentWindow.Last().OpenTime)
                .Take(config.MlLookbackPeriod / GetAggregationFactor(config.PrimaryTimeframe, config.HigherTimeframe))
                .Select(k => (double)k.ClosePrice)
                .ToArray();

            var higherFastMa = CalculateEma(higherCloses, config.FastMAPeriod);
            var higherSlowMa = CalculateEma(higherCloses, config.SlowMAPeriod);
            float higherTrend = (float)(higherFastMa - higherSlowMa);

            float marketSentiment = (float)((closes.Last() - adxvma) / (adxvma * 0.01));

            float fearGreedIndex = await GetFearGreedIndex();
            float sp500Correlation = await CalculateSP500Correlation(closes);
            float fundingRate = await GetFundingRate();

            double currentPrice = closes.Last();
            double futureMaxPrice = futurePrices.Max();
            double futureMinPrice = futurePrices.Min();
            double futureAvgPrice = futurePrices.Average();

            double atrValue = (double)CalculateATR(currentWindow.TakeLast(14).ToList(), 14);
            bool willRise = (futureMaxPrice - currentPrice) > (currentPrice - futureMinPrice) &&
                          (futureAvgPrice - currentPrice) > atrValue * 0.5;

            if (willRise && positiveCount > negativeCount * config.MlPositiveClassWeight) continue;
            if (!willRise && negativeCount > positiveCount / config.MlPositiveClassWeight) continue;

            if (willRise) positiveCount++;
            else negativeCount++;

            mlData.Add(new EnhancedMarketData
            {
                Open = (float)currentWindow.Last().OpenPrice,
                High = (float)currentWindow.Last().HighPrice,
                Low = (float)currentWindow.Last().LowPrice,
                Close = (float)currentWindow.Last().ClosePrice,
                Volume = (float)currentWindow.Last().Volume,
                SMI = (float)smi,
                SMISignal = (float)smiSignal,
                VWMA = (float)vwma,
                WRSI = (float)wrsi,
                ADXVMA = (float)adxvma,
                BBUpper = (float)upperBB,
                BBLower = (float)lowerBB,
                ATR = atr,
                VolumeChange = volumeChange,
                HigherTrend = higherTrend,
                MarketSentiment = marketSentiment,
                ADX = adx,
                FearGreedIndex = fearGreedIndex,
                SP500Correlation = sp500Correlation,
                FundingRate = fundingRate,
                OBV = obv,
                VWAP = vwap,
                Target = willRise
            });
        }

        logger.LogInformation($"Подготовлено {mlData.Count} записей для обучения ML (Позитивные: {positiveCount}, Негативные: {negativeCount})");
        return mlData;
    }

    // Обновленный метод проверки времени
    private static bool IsOptimalTradingTime()
    {
        DateTime utcNow = DateTime.UtcNow;
        double currentHour = utcNow.Hour + utcNow.Minute / 60.0;

        bool inMainSession = currentHour >= (double)config.OptimalTradeStartHour &&
                            currentHour < (double)config.OptimalTradeEndHour;

        // Фильтр ночного времени (23:00 - 04:00 UTC)
        bool inNightSession = config.EnableNightFilter &&
                            (currentHour >= 23 || currentHour < 4);

        return inMainSession && !inNightSession;
    }

    // Новый метод генерации сигналов
    private static (bool isBullish, bool isBearish) GenerateSignals(
        List<IBinanceKline> primaryKlines,
        List<IBinanceKline> higherKlines,
        List<IBinanceKline> lowerKlines,
        decimal atr,
        decimal currentPrice,
        bool isSupertrendBullish)
    {
        var primaryCloses = primaryKlines.Select(k => (double)k.ClosePrice).ToArray();
        var higherCloses = higherKlines.Select(k => (double)k.ClosePrice).ToArray();

        var primaryFastMa = CalculateEma(primaryCloses, config.FastMAPeriod);
        var primarySlowMa = CalculateEma(primaryCloses, config.SlowMAPeriod);
        var (smi, smiSignal) = CalculateSMI(primaryCloses, 14, 3);
        var vwma = CalculateVWMA(primaryKlines, 20);
        var wrsi = CalculateWRSI(primaryCloses, config.RSIPeriod);
        var adxvma = CalculateADXVMA(primaryCloses, primaryKlines, config.AdxPeriod, 20);

        var higherFastMa = CalculateEma(higherCloses, config.FastMAPeriod);
        var higherSlowMa = CalculateEma(higherCloses, config.SlowMAPeriod);

        bool isStrongTrend = Math.Abs(higherFastMa - higherSlowMa) > (double)atr * 0.5;
        bool isHigherTrendBullish = higherFastMa > higherSlowMa;

        // Новые условия на основе SMI и ADXVMA
        bool isPrimaryBullish =
            primaryFastMa > primarySlowMa &&
            currentPrice > (decimal)adxvma &&
            wrsi < config.OverboughtLevel &&
            smi > smiSignal &&
            smi > 40;

        bool isPrimaryBearish =
            primaryFastMa < primarySlowMa &&
            currentPrice < (decimal)adxvma &&
            wrsi > config.OversoldLevel &&
            smi < smiSignal &&
            smi < 60;

        bool isBullish = isHigherTrendBullish &&
                        isPrimaryBullish &&
                        isStrongTrend &&
                        isSupertrendBullish;

        bool isBearish = !isHigherTrendBullish &&
                        isPrimaryBearish &&
                        isStrongTrend &&
                        !isSupertrendBullish;

        return (isBullish, isBearish);
    }

    // Обновленный метод тренировки модели
    private static void TrainEnhancedModel(List<EnhancedMarketData> trainingData)
    {
        try
        {
            if (trainingData.Count < config.MlMinTrainingSamples)
            {
                logger.LogWarning($"Слишком мало данных для обучения: {trainingData.Count} записей (требуется {config.MlMinTrainingSamples})");
                return;
            }

            IDataView dataView = mlContext.Data.LoadFromEnumerable(trainingData);

            var dataProcessPipeline = mlContext.Transforms
                .Concatenate("Features",
                    nameof(EnhancedMarketData.Open),
                    nameof(EnhancedMarketData.High),
                    nameof(EnhancedMarketData.Low),
                    nameof(EnhancedMarketData.Close),
                    nameof(EnhancedMarketData.Volume),
                    nameof(EnhancedMarketData.SMI),
                    nameof(EnhancedMarketData.SMISignal),
                    nameof(EnhancedMarketData.VWMA),
                    nameof(EnhancedMarketData.WRSI),
                    nameof(EnhancedMarketData.ADXVMA),
                    nameof(EnhancedMarketData.BBUpper),
                    nameof(EnhancedMarketData.BBLower),
                    nameof(EnhancedMarketData.ATR),
                    nameof(EnhancedMarketData.VolumeChange),
                    nameof(EnhancedMarketData.HigherTrend),
                    nameof(EnhancedMarketData.MarketSentiment),
                    nameof(EnhancedMarketData.ADX),
                    nameof(EnhancedMarketData.FearGreedIndex),
                    nameof(EnhancedMarketData.SP500Correlation),
                    nameof(EnhancedMarketData.FundingRate),
                    nameof(EnhancedMarketData.OBV),
                    nameof(EnhancedMarketData.VWAP))
                .Append(mlContext.Transforms.NormalizeMinMax("Features"));

            var trainer = mlContext.BinaryClassification.Trainers.LightGbm(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 41,
                minimumExampleCountPerLeaf: 5,
                learningRate: 0.03,
                numberOfIterations: 300);

            var trainingPipeline = dataProcessPipeline.Append(trainer);

            mlModel = trainingPipeline.Fit(dataView);

            var cvResults = mlContext.BinaryClassification.CrossValidate(
                dataView,
                trainingPipeline,
                numberOfFolds: 5,
                labelColumnName: "Label");

            var avgAccuracy = cvResults.Average(r => r.Metrics.Accuracy);
            var avgAuc = cvResults.Average(r => r.Metrics.AreaUnderRocCurve);
            var avgF1Score = cvResults.Average(r => r.Metrics.F1Score);
            var avgLogLoss = cvResults.Average(r => r.Metrics.LogLoss);

            logger.LogInformation($"Модель обучена. Средние метрики:\n" +
                                $"Точность: {avgAccuracy:P2}\n" +
                                $"AUC: {avgAuc:F2}\n" +
                                $"F1-Score: {avgF1Score:F2}\n" +
                                $"LogLoss: {avgLogLoss:F4}");

            mlContext.Model.Save(mlModel, dataView.Schema, "EnhancedMarketPredictionModel.zip");
            logger.LogInformation("Модель сохранена в файл EnhancedMarketPredictionModel.zip");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при обучении модели");
            throw;
        }
    }

    private static async Task TrainInitialModel(BinanceRestClient binanceClient)
    {
        try
        {
            logger.LogInformation("Начало обучения начальной модели машинного обучения...");

            var historicalData = await GetAllHistoricalData(binanceClient);
            if (historicalData == null || historicalData.Count < config.MlMinTrainingSamples)
            {
                logger.LogError($"Не удалось получить достаточно данных для обучения. Нужно: {config.MlMinTrainingSamples}, есть: {historicalData?.Count ?? 0}");
                return;
            }

            var mlData = await PrepareEnhancedMLDataAsync(historicalData);
            if (mlData.Count == 0)
            {
                logger.LogError("Не удалось подготовить данные для обучения модели");
                return;
            }

            TrainEnhancedModel(mlData);
            lastModelTrainingTime = DateTime.Now;
            logger.LogInformation("Начальная модель машинного обучения успешно обучена");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при обучении начальной модели");
        }
    }

    private static List<IBinanceKline> AggregateKlinesToHigherTimeframe(List<IBinanceKline> klines, KlineInterval higherTimeframe)
    {
        var result = new List<IBinanceKline>();
        if (klines.Count == 0) return result;

        TimeSpan interval = higherTimeframe.ToTimeSpan();
        DateTime currentIntervalStart = klines.First().OpenTime;
        DateTime nextIntervalStart = currentIntervalStart.Add(interval);

        List<IBinanceKline> currentGroup = new List<IBinanceKline>();

        foreach (var kline in klines)
        {
            if (kline.OpenTime >= nextIntervalStart)
            {
                if (currentGroup.Count > 0)
                {
                    result.Add(CreateAggregatedKline(currentGroup, currentIntervalStart));
                    currentGroup.Clear();
                }

                currentIntervalStart = nextIntervalStart;
                nextIntervalStart = currentIntervalStart.Add(interval);
            }

            currentGroup.Add(kline);
        }

        if (currentGroup.Count > 0)
        {
            result.Add(CreateAggregatedKline(currentGroup, currentIntervalStart));
        }

        return result;
    }

    private static IBinanceKline CreateAggregatedKline(List<IBinanceKline> group, DateTime openTime)
    {
        return new BinanceStreamKline()
        {
            OpenTime = openTime,
            CloseTime = group.Last().CloseTime,
            OpenPrice = group.First().OpenPrice,
            ClosePrice = group.Last().ClosePrice,
            HighPrice = group.Max(k => k.HighPrice),
            LowPrice = group.Min(k => k.LowPrice),
            Volume = group.Sum(k => k.Volume),
            QuoteVolume = group.Sum(k => k.QuoteVolume),
            TradeCount = (int)group.Sum(k => k.TradeCount),
            TakerBuyBaseVolume = group.Sum(k => k.TakerBuyBaseVolume),
            TakerBuyQuoteVolume = group.Sum(k => k.TakerBuyQuoteVolume),
            Final = true
        };
    }

    private static int GetAggregationFactor(KlineInterval lower, KlineInterval higher)
    {
        var minutesMap = new Dictionary<KlineInterval, int>
        {
            { KlineInterval.OneMinute, 1 },
            { KlineInterval.ThreeMinutes, 3 },
            { KlineInterval.FiveMinutes, 5 },
            { KlineInterval.FifteenMinutes, 15 },
            { KlineInterval.ThirtyMinutes, 30 },
            { KlineInterval.OneHour, 60 },
            { KlineInterval.TwoHour, 120 },
            { KlineInterval.FourHour, 240 },
            { KlineInterval.OneDay, 1440 }
        };

        if (!minutesMap.ContainsKey(lower)) return 1;
        if (!minutesMap.ContainsKey(higher)) return 1;

        return minutesMap[higher] / minutesMap[lower];
    }

    public static (decimal[] supertrend, bool[] direction) CalculateSupertrend(
        List<IBinanceKline> klines,
        int period = 10,
        decimal multiplier = 3.0m)
    {
        int count = klines.Count;
        if (count < period)
        {
            return (new decimal[count], new bool[count]);
        }

        decimal[] supertrend = new decimal[count];
        bool[] direction = new bool[count];
        decimal[] upperBand = new decimal[count];
        decimal[] lowerBand = new decimal[count];
        decimal[] basicUpperBand = new decimal[count];
        decimal[] basicLowerBand = new decimal[count];
        decimal[] closePrices = klines.Select(k => k.ClosePrice).ToArray();
        decimal[] highPrices = klines.Select(k => k.HighPrice).ToArray();
        decimal[] lowPrices = klines.Select(k => k.LowPrice).ToArray();

        decimal[] trueRanges = new decimal[count];
        trueRanges[0] = highPrices[0] - lowPrices[0];

        for (int i = 1; i < count; i++)
        {
            decimal highLow = highPrices[i] - lowPrices[i];
            decimal highClose = Math.Abs(highPrices[i] - closePrices[i - 1]);
            decimal lowClose = Math.Abs(lowPrices[i] - closePrices[i - 1]);
            trueRanges[i] = Math.Max(highLow, Math.Max(highClose, lowClose));
        }

        decimal[] atr = new decimal[count];
        decimal atrSum = 0;

        for (int i = 0; i < period; i++)
        {
            atrSum += trueRanges[i];
        }
        atr[period - 1] = atrSum / period;

        for (int i = period; i < count; i++)
        {
            atr[i] = (atr[i - 1] * (period - 1) + trueRanges[i]) / period;
        }

        for (int i = 0; i < count; i++)
        {
            decimal medianPrice = (highPrices[i] + lowPrices[i]) / 2;
            basicUpperBand[i] = medianPrice + multiplier * atr[i];
            basicLowerBand[i] = medianPrice - multiplier * atr[i];
        }

        upperBand[0] = basicUpperBand[0];
        lowerBand[0] = basicLowerBand[0];

        for (int i = 1; i < count; i++)
        {
            if (basicUpperBand[i] < upperBand[i - 1] || closePrices[i - 1] > upperBand[i - 1])
            {
                upperBand[i] = basicUpperBand[i];
            }
            else
            {
                upperBand[i] = upperBand[i - 1];
            }

            if (basicLowerBand[i] > lowerBand[i - 1] || closePrices[i - 1] < lowerBand[i - 1])
            {
                lowerBand[i] = basicLowerBand[i];
            }
            else
            {
                lowerBand[i] = lowerBand[i - 1];
            }
        }

        supertrend[0] = closePrices[0] > upperBand[0] ? lowerBand[0] : upperBand[0];
        direction[0] = closePrices[0] > upperBand[0];

        for (int i = 1; i < count; i++)
        {
            if (supertrend[i - 1] == upperBand[i - 1] && closePrices[i] <= upperBand[i])
            {
                supertrend[i] = upperBand[i];
                direction[i] = false;
            }
            else if (supertrend[i - 1] == upperBand[i - 1] && closePrices[i] > upperBand[i])
            {
                supertrend[i] = lowerBand[i];
                direction[i] = true;
            }
            else if (supertrend[i - 1] == lowerBand[i - 1] && closePrices[i] >= lowerBand[i])
            {
                supertrend[i] = lowerBand[i];
                direction[i] = true;
            }
            else if (supertrend[i - 1] == lowerBand[i - 1] && closePrices[i] < lowerBand[i])
            {
                supertrend[i] = upperBand[i];
                direction[i] = false;
            }
            else
            {
                supertrend[i] = supertrend[i - 1];
                direction[i] = direction[i - 1];
            }
        }

        return (supertrend, direction);
    }

   

    private static async Task OptimizeParameters(BinanceRestClient binanceClient, TelegramBotClient telegramBot)
    {
        try
        {
            logger.LogInformation("Запуск оптимизации параметров...");

            var allKlines = await GetAllHistoricalData(binanceClient);
            if (allKlines == null || allKlines.Count < 1000)
            {
                logger.LogError("Не удалось получить достаточно данных для оптимизации");
                return;
            }

            var trainSize = (int)(allKlines.Count * 0.6);
            var valSize = (int)(allKlines.Count * 0.2);

            var trainKlines = allKlines.Take(trainSize).ToList();
            var valKlines = allKlines.Skip(trainSize).Take(valSize).ToList();
            var testKlines = allKlines.Skip(trainSize + valSize).ToList();

            logger.LogInformation("=== БАКТЕСТ ПЕРЕД ОПТИМИЗАЦИЕЙ ===");
            var defaultParams = new TradingParams(
                config.FastMAPeriod,
                config.SlowMAPeriod,
                config.RSIPeriod,
                config.OverboughtLevel,
                config.OversoldLevel,
                config.BbPeriod,
                config.BbStdDev);

            var defaultScore = EvaluateParameters(trainKlines, valKlines, testKlines, defaultParams);
            logger.LogInformation($"Результат до оптимизации: {defaultScore:F2} с параметрами: {defaultParams}");

            var random = new Random();
            var bestScore = double.MinValue;
            var bestParams = defaultParams;
            var bestPopulation = new List<(double Score, TradingParams Params)>();

            for (int generation = 0; generation < config.OptimizationGenerations; generation++)
            {
                logger.LogInformation($"Поколение {generation + 1}/{config.OptimizationGenerations}");

                var population = new List<TradingParams>();

                if (generation == 0)
                {
                    for (int i = 0; i < config.OptimizationPopulationSize; i++)
                    {
                        population.Add(new TradingParams(
                            random.Next(config.FastMAPeriodRange[0], config.FastMAPeriodRange[1]),
                            random.Next(config.SlowMAPeriodRange[0], config.SlowMAPeriodRange[1]),
                            random.Next(config.RSIPeriodRange[0], config.RSIPeriodRange[1]),
                            config.OverboughtLevelRange[0] + random.NextDouble() * (config.OverboughtLevelRange[1] - config.OverboughtLevelRange[0]),
                            config.OversoldLevelRange[0] + random.NextDouble() * (config.OversoldLevelRange[1] - config.OversoldLevelRange[0]),
                            random.Next(10, 30),
                            1.5 + random.NextDouble() * 1.5));
                    }
                }
                else
                {
                    int eliteCount = (int)(config.OptimizationPopulationSize * 0.2);
                    for (int i = 0; i < eliteCount; i++)
                    {
                        population.Add(bestPopulation[i].Params);
                    }

                    double mutationStrength = 1.0 - (generation / (double)config.OptimizationGenerations);

                    for (int i = eliteCount; i < config.OptimizationPopulationSize; i++)
                    {
                        var parent = bestPopulation[random.Next(bestPopulation.Count / 2)].Params;
                        population.Add(MutateParams(parent, random, mutationStrength));
                    }
                }

                var results = new List<(double Score, TradingParams Params)>();
                Parallel.ForEach(population, paramSet =>
                {
                    var result = EvaluateParameters(trainKlines, valKlines, testKlines, paramSet);
                    lock (results)
                    {
                        results.Add((result, paramSet));
                        logger.LogDebug($"Оценка параметров {paramSet}: {result:F2}");
                    }
                });

                results = results.OrderByDescending(r => r.Score).ToList();
                bestPopulation = results;

                foreach (var result in results)
                {
                    if (result.Score > bestScore)
                    {
                        bestScore = result.Score;
                        bestParams = result.Params;
                        logger.LogInformation($"Новый лучший результат: {bestScore:F2} с параметрами: {bestParams}");
                    }
                }
            }

            logger.LogInformation("=== БАКТЕСТ ПОСЛЕ ОПТИМИЗАЦИИ ===");
            var optimizedScore = EvaluateParameters(trainKlines, valKlines, testKlines, bestParams);
            logger.LogInformation($"Результат после оптимизации: {optimizedScore:F2} с параметрами: {bestParams}");

            await telegramBot.SendMessage(
                chatId: config.TelegramChatId,
                text: $"🎯 Результаты оптимизации {config.Symbol}\n" +
                      $"До оптимизации: {defaultScore:F2}\n" +
                      $"После оптимизации: {bestScore:F2}\n" +
                      $"Параметры: {bestParams}");

            config.FastMAPeriod = bestParams.FastMAPeriod;
            config.SlowMAPeriod = bestParams.SlowMAPeriod;
            config.RSIPeriod = bestParams.RSIPeriod;
            config.OverboughtLevel = bestParams.OverboughtLevel;
            config.OversoldLevel = bestParams.OversoldLevel;
            config.BbPeriod = bestParams.BbPeriod;
            config.BbStdDev = bestParams.BbStdDev;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка оптимизации параметров");
            await telegramBot.SendMessage(config.TelegramChatId,
                $"❌ Ошибка оптимизации: {ex.Message}");
        }
    }

    private static double EvaluateParameters(List<IBinanceKline> trainKlines, List<IBinanceKline> valKlines, List<IBinanceKline> testKlines, TradingParams parameters)
    {
        try
        {
            var trainResult = BacktestWithParams(trainKlines, parameters);

            logger.LogInformation($"Оценка параметров {parameters} на train: " +
                                 $"Profit={trainResult.ProfitRatio:F2}, " +
                                 $"WinRate={trainResult.WinRate:F2}");

            if (trainResult.WinRate < config.OptimizationMinWinRate)
            {
                logger.LogDebug($"Низкий WinRate: {trainResult.WinRate:F2} < {config.OptimizationMinWinRate}");
                return trainResult.WinRate;
            }

            if (trainResult.ProfitRatio < config.OptimizationMinProfitRatio)
            {
                logger.LogDebug($"Низкий ProfitRatio: {trainResult.ProfitRatio:F2} < {config.OptimizationMinProfitRatio}");
                return trainResult.ProfitRatio;
            }

            var valResult = BacktestWithParams(valKlines, parameters);
            var testResult = BacktestWithParams(testKlines, parameters);

            double stabilityFactor = 1.0 - Math.Abs(trainResult.ProfitRatio - testResult.ProfitRatio);
            double score = testResult.SharpeRatio * config.OptimizationSharpeWeight +
                          (testResult.ProfitRatio - 1) * config.OptimizationProfitWeight +
                          testResult.WinRate * config.OptimizationWinRateWeight -
                          testResult.MaxDrawdown * config.OptimizationDrawdownWeight;

            score *= stabilityFactor;

            if (double.IsNaN(score)) score = 0;

            logger.LogInformation($"Оценка параметров {parameters}: Score={score:F2}, " +
                                 $"Profit={testResult.ProfitRatio:F2}, " +
                                 $"Sharpe={testResult.SharpeRatio:F2}, " +
                                 $"WinRate={testResult.WinRate:F2}, " +
                                 $"DD={testResult.MaxDrawdown:F2}%, " +
                                 $"Stability={stabilityFactor:F2}");

            return score;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Ошибка оценки параметров {parameters}");
            return 0;
        }
    }

    private static BacktestResult BacktestWithParams(List<IBinanceKline> klines, TradingParams parameters)
    {
        decimal balance = config.InitialBalance;
        decimal position = 0;
        decimal entryPrice = 0;
        var equityCurve = new List<decimal> { balance };
        int tradesCount = 0;
        int profitableTrades = 0;
        decimal totalCommission = 0;
        decimal maxBalance = balance;
        decimal maxDrawdown = 0;

        int requiredBars = new[] {
            parameters.SlowMAPeriod,
            parameters.RSIPeriod,
            parameters.BbPeriod,
            config.VolatilityPeriod
        }.Max() + 1;

        if (klines.Count < requiredBars)
        {
            logger.LogWarning($"Недостаточно данных для оценки. Требуется: {requiredBars}, получено: {klines.Count}");
            return new BacktestResult(0, 0, 0, 0);
        }

        var higherTimeframeKlines = AggregateKlinesToHigherTimeframe(klines, config.HigherTimeframe);
        var (supertrendValues, supertrendDirections) = CalculateSupertrend(
            klines,
            config.SupertrendPeriod,
            config.SupertrendMultiplier);

        for (int i = requiredBars; i < klines.Count; i++)
        {
            var currentKline = klines[i];
            var previousKlines = klines.Take(i).ToList();

            if (!CheckVolumeFilter(previousKlines, i)) continue;
            if (!CheckVolatilityFilter(previousKlines, i)) continue;

            var closePrices = previousKlines.Select(k => (double)k.ClosePrice).ToArray();
            var currentPrice = (double)currentKline.ClosePrice;

            var fastMa = CalculateEma(closePrices, parameters.FastMAPeriod);
            var slowMa = CalculateEma(closePrices, parameters.SlowMAPeriod);
            var (smi, smiSignal) = CalculateSMI(closePrices, 14, 3);
            var vwma = CalculateVWMA(previousKlines, 20);
            var wrsi = CalculateWRSI(closePrices, parameters.RSIPeriod);
            var adxvma = CalculateADXVMA(closePrices, previousKlines, config.AdxPeriod, 20);
            var (upperBand, middleBand, lowerBand) = CalculateBollingerBands(closePrices, parameters.BbPeriod, parameters.BbStdDev);
            var adx = CalculateADX(previousKlines.Skip(i - config.AdxPeriod).Take(config.AdxPeriod).ToList(), config.AdxPeriod);
            bool isSupertrendBullish = i < supertrendDirections.Length ? supertrendDirections[i] : false;

            var atrPeriod = (int)Math.Ceiling(24 / (decimal)config.PrimaryTimeframe.ToTimeSpan().TotalHours);
            var atr = CalculateATR(previousKlines.Skip(i - atrPeriod).Take(atrPeriod).ToList(), atrPeriod);
            var safeAtr = atr > 0 ? atr : (decimal)currentPrice * config.MinAtrPercent;

            var higherKlinesForTrend = higherTimeframeKlines
                .Where(k => k.OpenTime <= currentKline.OpenTime)
                .TakeLast(parameters.SlowMAPeriod / 2)
                .ToList();

            if (higherKlinesForTrend.Count < 5) continue;

            var higherCloses = higherKlinesForTrend.Select(k => (double)k.ClosePrice).ToArray();
            var higherFastMa = CalculateEma(higherCloses, parameters.FastMAPeriod / 2);
            var higherSlowMa = CalculateEma(higherCloses, parameters.SlowMAPeriod / 2);

            bool isStrongTrend = Math.Abs(higherFastMa - higherSlowMa) > (double)safeAtr * 0.5;

            if (position != 0)
            {
                bool shouldClose = false;
                decimal exitPrice = 0;
                string exitReason = "";

                var holdTime = currentKline.OpenTime - tradeHistory.Last().Timestamp;
                if (holdTime.TotalHours >= 24)
                {
                    exitPrice = (decimal)currentPrice;
                    exitReason = "Time Exit";
                    shouldClose = true;
                }
                else if (position > 0)
                {
                    if ((decimal)currentPrice <= entryPrice - safeAtr * config.AtrMultiplierSL)
                    {
                        exitPrice = entryPrice - safeAtr * config.AtrMultiplierSL;
                        exitReason = "SL";
                        shouldClose = true;
                    }
                    else if ((decimal)currentPrice >= entryPrice + safeAtr * config.AtrMultiplierTP)
                    {
                        exitPrice = entryPrice + safeAtr * config.AtrMultiplierTP;
                        exitReason = "TP";
                        shouldClose = true;
                    }
                    else if (!isSupertrendBullish)
                    {
                        exitPrice = (decimal)currentPrice;
                        exitReason = "Supertrend Exit";
                        shouldClose = true;
                    }
                    else if ((decimal)currentPrice >= entryPrice + safeAtr * config.PartialTakeProfitLevel)
                    {
                        decimal partialQuantity = Math.Abs(position) * config.PartialTakeProfitFactor;
                        decimal partialPnl = partialQuantity * ((decimal)currentPrice - entryPrice);
                        decimal commission = partialQuantity * (decimal)currentPrice * config.CommissionPercent / 100m;
                        totalCommission += commission;
                        partialPnl -= commission;

                        balance += partialPnl;
                        position -= partialQuantity;
                        tradesCount++;
                        if (partialPnl > 0) profitableTrades++;

                        tradeHistory.Add(new TradeRecord(
                            currentKline.OpenTime,
                            "SELL (Partial)",
                            partialQuantity,
                            entryPrice,
                            (decimal)currentPrice,
                            0, 0, partialPnl,
                            commission,
                            $"Частичное закрытие длинной позиции. ATR: {safeAtr:F2}"));

                        entryPrice = (decimal)currentPrice;
                    }
                }
                else
                {
                    if ((decimal)currentPrice >= entryPrice + safeAtr * config.AtrMultiplierSL)
                    {
                        exitPrice = entryPrice + safeAtr * config.AtrMultiplierSL;
                        exitReason = "SL";
                        shouldClose = true;
                    }
                    else if ((decimal)currentPrice <= entryPrice - safeAtr * config.AtrMultiplierTP)
                    {
                        exitPrice = entryPrice - safeAtr * config.AtrMultiplierTP;
                        exitReason = "TP";
                        shouldClose = true;
                    }
                    else if (isSupertrendBullish)
                    {
                        exitPrice = (decimal)currentPrice;
                        exitReason = "Supertrend Exit";
                        shouldClose = true;
                    }
                    else if ((decimal)currentPrice <= entryPrice - safeAtr * config.PartialTakeProfitLevel)
                    {
                        decimal partialQuantity = Math.Abs(position) * config.PartialTakeProfitFactor;
                        decimal partialPnl = partialQuantity * (entryPrice - (decimal)currentPrice);
                        decimal commission = partialQuantity * (decimal)currentPrice * config.CommissionPercent / 100m;
                        totalCommission += commission;
                        partialPnl -= commission;

                        balance += partialPnl;
                        position += partialQuantity;
                        tradesCount++;
                        if (partialPnl > 0) profitableTrades++;

                        tradeHistory.Add(new TradeRecord(
                            currentKline.OpenTime,
                            "BUY (Partial)",
                            partialQuantity,
                            entryPrice,
                            (decimal)currentPrice,
                            0, 0, partialPnl,
                            commission,
                            $"Частичное закрытие короткой позиции. ATR: {safeAtr:F2}"));

                        entryPrice = (decimal)currentPrice;
                    }
                }

                if (shouldClose)
                {
                    decimal pnl = position > 0
                        ? position * ((decimal)currentPrice - entryPrice)
                        : position * (entryPrice - (decimal)currentPrice);

                    decimal commission = Math.Abs(position) * (decimal)currentPrice * config.CommissionPercent / 100m;
                    totalCommission += commission;
                    pnl -= commission;

                    balance += pnl;
                    tradesCount++;
                    if (pnl > 0) profitableTrades++;

                    if (balance > maxBalance) maxBalance = balance;
                    decimal drawdown = (maxBalance - balance) / maxBalance * 100;
                    if (drawdown > maxDrawdown) maxDrawdown = drawdown;

                    tradeHistory.Add(new TradeRecord(
                        currentKline.OpenTime,
                        position > 0 ? $"SELL ({exitReason})" : $"BUY ({exitReason})",
                        Math.Abs(position),
                        entryPrice,
                        (decimal)currentPrice,
                        position > 0 ? entryPrice - safeAtr * config.AtrMultiplierSL : entryPrice + safeAtr * config.AtrMultiplierSL,
                        position > 0 ? entryPrice + safeAtr * config.AtrMultiplierTP : entryPrice - safeAtr * config.AtrMultiplierTP,
                        pnl,
                        commission,
                        $"Закрытие по {exitReason}. ATR: {safeAtr:F2}"));

                    position = 0;
                    equityCurve.Add(balance);
                    continue;
                }
            }

            bool isHigherTrendBullish = higherFastMa > higherSlowMa;
            bool isHigherTrendBearish = higherFastMa < higherSlowMa;

            bool isPrimaryBullish =
                fastMa > slowMa &&
                currentPrice > adxvma &&
                wrsi < parameters.OverboughtLevel &&
                smi > smiSignal &&
                smi > 40;

            bool isPrimaryBearish =
                fastMa < slowMa &&
                currentPrice < adxvma &&
                wrsi > parameters.OversoldLevel &&
                smi < smiSignal &&
                smi < 60;

            bool isBullish = isHigherTrendBullish && isPrimaryBullish && isStrongTrend && isSupertrendBullish;
            bool isBearish = isHigherTrendBearish && isPrimaryBearish && isStrongTrend && !isSupertrendBullish;

            if (isBullish && position <= 0)
            {
                if (position < 0)
                {
                    decimal pnl = position * ((decimal)currentPrice - entryPrice);
                    decimal commission = Math.Abs(position) * (decimal)currentPrice * config.CommissionPercent / 100m;
                    totalCommission += commission;
                    pnl -= commission;

                    balance += pnl;
                    tradesCount++;
                    if (pnl > 0) profitableTrades++;

                    tradeHistory.Add(new TradeRecord(
                        currentKline.OpenTime,
                        "BUY (Close)",
                        Math.Abs(position),
                        entryPrice,
                        (decimal)currentPrice,
                        0, 0, pnl,
                        commission,
                        "Закрытие короткой позиции перед открытием длинной"));
                }

                decimal maxPosition = balance * config.MaxPositionSizePercent / (decimal)currentPrice;

                if (maxPosition < config.MinOrderSize)
                {
                    logger.LogDebug($"Пропуск сделки: недостаточно средств для минимального ордера. Нужно: {config.MinOrderSize}, доступно: {maxPosition}");
                    continue;
                }

                decimal riskBasedQty = (balance * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
                decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

                position = quantity;
                entryPrice = (decimal)currentPrice;

                decimal entryCommission = quantity * entryPrice * config.CommissionPercent / 100m;
                totalCommission += entryCommission;
                balance -= entryCommission;

                tradeHistory.Add(new TradeRecord(
                    currentKline.OpenTime,
                    "BUY",
                    quantity,
                    entryPrice,
                    0,
                    entryPrice - safeAtr * config.AtrMultiplierSL,
                    entryPrice + safeAtr * config.AtrMultiplierTP,
                    -entryCommission,
                    entryCommission,
                    $"Открытие длинной позиции. ATR: {safeAtr:F2}"));
            }
            else if (isBearish && position >= 0)
            {
                if (position > 0)
                {
                    decimal pnl = position * ((decimal)currentPrice - entryPrice);
                    decimal commission = position * (decimal)currentPrice * config.CommissionPercent / 100m;
                    totalCommission += commission;
                    pnl -= commission;

                    balance += pnl;
                    tradesCount++;
                    if (pnl > 0) profitableTrades++;

                    tradeHistory.Add(new TradeRecord(
                        currentKline.OpenTime,
                        "SELL (Close)",
                        position,
                        entryPrice,
                        (decimal)currentPrice,
                        0, 0, pnl,
                        commission,
                        "Закрытие длинной позиции перед открытием короткой"));
                }

                decimal maxPosition = balance * config.MaxPositionSizePercent / (decimal)currentPrice;

                if (maxPosition < config.MinOrderSize)
                {
                    logger.LogDebug($"Пропуск сделки: недостаточно средств для минимального ордера. Нужно: {config.MinOrderSize}, доступно: {maxPosition}");
                    continue;
                }

                decimal riskBasedQty = (balance * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
                decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

                position = -quantity;
                entryPrice = (decimal)currentPrice;

                decimal entryCommission = quantity * entryPrice * config.CommissionPercent / 100m;
                totalCommission += entryCommission;
                balance -= entryCommission;

                tradeHistory.Add(new TradeRecord(
                    currentKline.OpenTime,
                    "SELL",
                    quantity,
                    entryPrice,
                    0,
                    entryPrice + safeAtr * config.AtrMultiplierSL,
                    entryPrice - safeAtr * config.AtrMultiplierTP,
                    -entryCommission,
                    entryCommission,
                    $"Открытие короткой позиции. ATR: {safeAtr:F2}"));
            }

            equityCurve.Add(balance + position * ((decimal)currentPrice - entryPrice));
        }

        if (position != 0)
        {
            var lastPrice = (double)klines.Last().ClosePrice;
            decimal pnl = position > 0
                ? position * ((decimal)lastPrice - entryPrice)
                : position * (entryPrice - (decimal)lastPrice);

            decimal commission = Math.Abs(position) * (decimal)lastPrice * config.CommissionPercent / 100m;
            totalCommission += commission;
            pnl -= commission;

            balance += pnl;
            tradesCount++;
            if (pnl > 0) profitableTrades++;

            tradeHistory.Add(new TradeRecord(
                klines.Last().OpenTime,
                position > 0 ? "SELL (Close)" : "BUY (Close)",
                Math.Abs(position),
                entryPrice,
                (decimal)lastPrice,
                0, 0, pnl,
                commission,
                "Принудительное закрытие позиции в конце теста"));
        }

        double profitRatio = (double)(balance / config.InitialBalance);
        double sharpeRatio = CalculateSharpeRatio(equityCurve);
        double winRate = tradesCount > 0 ? (double)profitableTrades / tradesCount : 0;
        double maxDrawdownPercent = (double)maxDrawdown;

        return new BacktestResult(profitRatio, sharpeRatio, winRate, maxDrawdownPercent);
    }

    private record BacktestResult(
        double ProfitRatio,
        double SharpeRatio,
        double WinRate,
        double MaxDrawdown);

    private static async Task RunBacktestUniversal(
        BinanceRestClient binanceClient,
        TelegramBotClient telegramBot,
        string description,
        TradingParams parameters = null,
        bool useMachineLearning = false)
    {
        try
        {
            parameters ??= new TradingParams(
                config.FastMAPeriod,
                config.SlowMAPeriod,
                config.RSIPeriod,
                config.OverboughtLevel,
                config.OversoldLevel,
                config.BbPeriod,
                config.BbStdDev);

            logger.LogInformation("=== НАЧАЛО БЭКТЕСТА ===");
            logger.LogInformation($"Описание: {description}");
            logger.LogInformation($"Параметры: {parameters}");
            if (useMachineLearning) logger.LogInformation("С ИСПОЛЬЗОВАНИЕМ МАШИННОГО ОБУЧЕНИЯ");
            logger.LogInformation($"Период: {config.BacktestStartDate:yyyy-MM-dd} - {config.BacktestEndDate:yyyy-MM-dd}");
            logger.LogInformation($"Таймфрейм: {config.BacktestInterval}");
            logger.LogInformation($"Начальный баланс: {config.InitialBalance}");

            logger.LogInformation("Загрузка исторических данных...");
            var allKlines = await GetAllHistoricalData(binanceClient);

            if (allKlines == null || allKlines.Count == 0)
            {
                logger.LogError("Не удалось получить исторические данные");
                await telegramBot.SendMessage(config.TelegramChatId, "❌ Ошибка: не удалось получить исторические данные");
                return;
            }

            logger.LogInformation($"Первая свеча: {allKlines.First().OpenTime}");
            logger.LogInformation($"Последняя свеча: {allKlines.Last().OpenTime}");
            logger.LogInformation($"Всего свечей: {allKlines.Count}");

            if (useMachineLearning)
            {
                logger.LogInformation("Подготовка модели машинного обучения...");
                var mlData = await PrepareEnhancedMLDataAsync(allKlines);
                if (mlData.Count == 0)
                {
                    logger.LogError("Не удалось подготовить данные для ML");
                    return;
                }
                TrainEnhancedModel(mlData);
            }

            decimal balance = config.InitialBalance;
            decimal position = 0;
            decimal entryPrice = 0;
            tradeHistory = new List<TradeRecord>();
            var equityCurve = new List<decimal> { balance };
            int signalsGenerated = 0;
            int tradesExecuted = 0;
            int mlConfirmations = 0;
            int mlRejections = 0;
            decimal totalCommission = 0;
            int profitableTrades = 0;
            decimal maxBalance = balance;
            decimal maxDrawdown = 0;

            int requiredBars = new[] {
                parameters.SlowMAPeriod,
                parameters.RSIPeriod,
                parameters.BbPeriod,
                config.VolatilityPeriod,
                useMachineLearning ? config.MlLookbackPeriod : 0
            }.Max() + 1;

            if (allKlines.Count < requiredBars)
            {
                logger.LogError($"Недостаточно данных. Требуется: {requiredBars}, получено: {allKlines.Count}");
                return;
            }

            var predictionEngine = useMachineLearning && mlModel != null ?
                mlContext.Model.CreatePredictionEngine<EnhancedMarketData, MarketPrediction>(mlModel) : null;
            var higherTimeframeKlines = AggregateKlinesToHigherTimeframe(allKlines, config.HigherTimeframe);
            var (supertrendValues, supertrendDirections) = CalculateSupertrend(
                allKlines,
                config.SupertrendPeriod,
                config.SupertrendMultiplier);

            logger.LogInformation("Начало обработки данных...");
            for (int i = requiredBars; i < allKlines.Count; i++)
            {
                var currentKline = allKlines[i];
                var previousKlines = allKlines.Take(i).ToList();
                var closePrices = previousKlines.Select(k => (double)k.ClosePrice).ToArray();
                var currentPrice = (double)currentKline.ClosePrice;

                var atr = CalculateATR(previousKlines.Skip(i - 20).Take(20).ToList(), 20);
                var safeAtr = atr > 0 ? atr : (decimal)currentPrice * config.MinAtrPercent;

                var fastMa = CalculateEma(closePrices, parameters.FastMAPeriod);
                var slowMa = CalculateEma(closePrices, parameters.SlowMAPeriod);
                var (smi, smiSignal) = CalculateSMI(closePrices, 14, 3);
                var vwma = CalculateVWMA(previousKlines, 20);
                var wrsi = CalculateWRSI(closePrices, parameters.RSIPeriod);
                var adxvma = CalculateADXVMA(closePrices, previousKlines, config.AdxPeriod, 20);
                var (upperBB, middleBB, lowerBB) = CalculateBollingerBands(closePrices, parameters.BbPeriod, parameters.BbStdDev);
                var adx = CalculateADX(previousKlines.Skip(i - config.AdxPeriod).Take(config.AdxPeriod).ToList(), config.AdxPeriod);
                bool isSupertrendBullish = i < supertrendDirections.Length ? supertrendDirections[i] : false;

                var higherKlinesForTrend = higherTimeframeKlines
                    .Where(k => k.OpenTime <= currentKline.OpenTime)
                    .TakeLast(parameters.SlowMAPeriod / 2)
                    .ToList();

                if (higherKlinesForTrend.Count < 5) continue;

                var higherCloses = higherKlinesForTrend.Select(k => (double)k.ClosePrice).ToArray();
                var higherFastMa = CalculateEma(higherCloses, parameters.FastMAPeriod / 2);
                var higherSlowMa = CalculateEma(higherCloses, parameters.SlowMAPeriod / 2);

                bool isStrongTrend = Math.Abs(higherFastMa - higherSlowMa) > (double)safeAtr * 0.5;
                bool isHigherTrendBullish = higherFastMa > higherSlowMa;
                bool isHigherTrendBearish = higherFastMa < higherSlowMa;

                bool mlConfirmation = true;

                bool isPrimaryBullish =
                    fastMa > slowMa &&
                    currentPrice > adxvma &&
                    wrsi < parameters.OverboughtLevel &&
                    smi > smiSignal &&
                    smi > 40;

                bool isPrimaryBearish =
                    fastMa < slowMa &&
                    currentPrice < adxvma &&
                    wrsi > parameters.OversoldLevel &&
                    smi < smiSignal &&
                    smi < 60;

                if (useMachineLearning && predictionEngine != null && (isPrimaryBullish || isPrimaryBearish))
                {
                    var mlInput = new EnhancedMarketData
                    {
                        Open = (float)currentKline.OpenPrice,
                        High = (float)currentKline.HighPrice,
                        Low = (float)currentKline.LowPrice,
                        Close = (float)currentKline.ClosePrice,
                        Volume = (float)currentKline.Volume,
                        SMI = (float)smi,
                        SMISignal = (float)smiSignal,
                        VWMA = (float)vwma,
                        WRSI = (float)wrsi,
                        ADXVMA = (float)adxvma,
                        BBUpper = (float)upperBB,
                        BBLower = (float)lowerBB,
                        ATR = (float)safeAtr,
                        VolumeChange = (float)(currentKline.Volume / previousKlines.Average(k => k.Volume) - 1),
                        HigherTrend = (float)(higherFastMa - higherSlowMa),
                        MarketSentiment = (float)((currentPrice - middleBB) / (middleBB * 0.01)),
                        ADX = (float)adx,
                        FearGreedIndex = await GetFearGreedIndex(),
                        SP500Correlation = await CalculateSP500Correlation(closePrices),
                        FundingRate = await GetFundingRate(),
                        OBV = (float)CalculateOBV(previousKlines.TakeLast(50).ToList()),
                        VWAP = (float)CalculateVWAP(previousKlines.TakeLast(50).ToList())
                    };

                    var prediction = predictionEngine.Predict(mlInput);
                    mlConfirmation = prediction.ConfirmedPrediction;

                    if (mlConfirmation)
                    {
                        mlConfirmations++;
                        logger.LogDebug($"ML подтвердил сигнал: {(isPrimaryBullish ? "BUY" : "SELL")} (Score: {prediction.Score:P2})");
                    }
                    else
                    {
                        mlRejections++;
                        logger.LogDebug($"ML отклонил сигнал: {(isPrimaryBullish ? "BUY" : "SELL")} (Score: {prediction.Score:P2})");
                    }
                }

                bool isBullish = (isHigherTrendBullish || !isHigherTrendBearish) &&
                                isPrimaryBullish &&
                                (!useMachineLearning || mlConfirmation) &&
                                isStrongTrend &&
                                isSupertrendBullish;

                bool isBearish = (isHigherTrendBearish || !isHigherTrendBullish) &&
                                isPrimaryBearish &&
                                (!useMachineLearning || mlConfirmation) &&
                                isStrongTrend &&
                                !isSupertrendBullish;

                if (isBullish)
                {
                    logger.LogDebug($"Обнаружен BUY сигнал на {currentKline.OpenTime}");
                    signalsGenerated++;
                }
                else if (isBearish)
                {
                    logger.LogDebug($"Обнаружен SELL сигнал на {currentKline.OpenTime}");
                    signalsGenerated++;
                }

                if (position != 0)
                {
                    bool shouldClose = false;
                    decimal exitPrice = 0;
                    string exitReason = "";

                    var holdTime = currentKline.OpenTime - tradeHistory.Last().Timestamp;
                    if (holdTime.TotalHours >= 24)
                    {
                        exitPrice = (decimal)currentPrice;
                        exitReason = "Time Exit";
                        shouldClose = true;
                    }
                    else if (position > 0)
                    {
                        if ((decimal)currentPrice <= entryPrice - safeAtr * config.AtrMultiplierSL)
                        {
                            exitPrice = entryPrice - safeAtr * config.AtrMultiplierSL;
                            exitReason = "SL";
                            shouldClose = true;
                        }
                        else if ((decimal)currentPrice >= entryPrice + safeAtr * config.AtrMultiplierTP)
                        {
                            exitPrice = entryPrice + safeAtr * config.AtrMultiplierTP;
                            exitReason = "TP";
                            shouldClose = true;
                        }
                        else if (!isSupertrendBullish)
                        {
                            exitPrice = (decimal)currentPrice;
                            exitReason = "Supertrend Exit";
                            shouldClose = true;
                        }
                        else if ((decimal)currentPrice >= entryPrice + safeAtr * config.PartialTakeProfitLevel)
                        {
                            decimal partialQuantity = Math.Abs(position) * config.PartialTakeProfitFactor;
                            decimal partialPnl = partialQuantity * ((decimal)currentPrice - entryPrice);
                            decimal commission = partialQuantity * (decimal)currentPrice * config.CommissionPercent / 100m;
                            totalCommission += commission;
                            partialPnl -= commission;

                            balance += partialPnl;
                            position -= partialQuantity;
                            tradesExecuted++;
                            if (partialPnl > 0) profitableTrades++;

                            tradeHistory.Add(new TradeRecord(
                                currentKline.OpenTime,
                                "SELL (Partial)",
                                partialQuantity,
                                entryPrice,
                                (decimal)currentPrice,
                                0, 0, partialPnl,
                                commission,
                                $"Частичное закрытие длинной позиции. ATR: {safeAtr:F2}"));

                            entryPrice = (decimal)currentPrice;
                        }
                    }
                    else
                    {
                        if ((decimal)currentPrice >= entryPrice + safeAtr * config.AtrMultiplierSL)
                        {
                            exitPrice = entryPrice + safeAtr * config.AtrMultiplierSL;
                            exitReason = "SL";
                            shouldClose = true;
                        }
                        else if ((decimal)currentPrice <= entryPrice - safeAtr * config.AtrMultiplierTP)
                        {
                            exitPrice = entryPrice - safeAtr * config.AtrMultiplierTP;
                            exitReason = "TP";
                            shouldClose = true;
                        }
                        else if (isSupertrendBullish)
                        {
                            exitPrice = (decimal)currentPrice;
                            exitReason = "Supertrend Exit";
                            shouldClose = true;
                        }
                        else if ((decimal)currentPrice <= entryPrice - safeAtr * config.PartialTakeProfitLevel)
                        {
                            decimal partialQuantity = Math.Abs(position) * config.PartialTakeProfitFactor;
                            decimal partialPnl = partialQuantity * (entryPrice - (decimal)currentPrice);
                            decimal commission = partialQuantity * (decimal)currentPrice * config.CommissionPercent / 100m;
                            totalCommission += commission;
                            partialPnl -= commission;

                            balance += partialPnl;
                            position += partialQuantity;
                            tradesExecuted++;
                            if (partialPnl > 0) profitableTrades++;

                            tradeHistory.Add(new TradeRecord(
                                currentKline.OpenTime,
                                "BUY (Partial)",
                                partialQuantity,
                                entryPrice,
                                (decimal)currentPrice,
                                0, 0, partialPnl,
                                commission,
                                $"Частичное закрытие короткой позиции. ATR: {safeAtr:F2}"));

                            entryPrice = (decimal)currentPrice;
                        }
                    }

                    if (shouldClose)
                    {
                        decimal pnl = position > 0
                            ? position * ((decimal)currentPrice - entryPrice)
                            : position * (entryPrice - (decimal)currentPrice);

                        decimal commission = Math.Abs(position) * (decimal)currentPrice * config.CommissionPercent / 100m;
                        totalCommission += commission;
                        pnl -= commission;

                        balance += pnl;
                        tradesExecuted++;
                        if (pnl > 0) profitableTrades++;

                        if (balance > maxBalance) maxBalance = balance;
                        decimal drawdown = (maxBalance - balance) / maxBalance * 100;
                        if (drawdown > maxDrawdown) maxDrawdown = drawdown;

                        tradeHistory.Add(new TradeRecord(
                            currentKline.OpenTime,
                            position > 0 ? $"SELL ({exitReason})" : $"BUY ({exitReason})",
                            Math.Abs(position),
                            entryPrice,
                            (decimal)currentPrice,
                            position > 0 ? entryPrice - safeAtr * config.AtrMultiplierSL : entryPrice + safeAtr * config.AtrMultiplierSL,
                            position > 0 ? entryPrice + safeAtr * config.AtrMultiplierTP : entryPrice - safeAtr * config.AtrMultiplierTP,
                            pnl,
                            commission,
                            $"Закрытие по {exitReason}. ATR: {safeAtr:F2}"));

                        position = 0;
                        equityCurve.Add(balance);
                        continue;
                    }
                }

                if (isBullish && position <= 0)
                {
                    signalsGenerated++;

                    if (position < 0)
                    {
                        decimal pnl = position * ((decimal)currentPrice - entryPrice);
                        decimal commission = Math.Abs(position) * (decimal)currentPrice * config.CommissionPercent / 100m;
                        totalCommission += commission;
                        pnl -= commission;

                        balance += pnl;
                        tradesExecuted++;
                        if (pnl > 0) profitableTrades++;

                        tradeHistory.Add(new TradeRecord(
                            currentKline.OpenTime,
                            "BUY (Close)",
                            Math.Abs(position),
                            entryPrice,
                            (decimal)currentPrice,
                            0, 0, pnl,
                            commission,
                            "Закрытие короткой позиции перед открытием длинной"));
                    }

                    decimal maxPosition = balance * config.MaxPositionSizePercent / (decimal)currentPrice;

                    if (maxPosition < config.MinOrderSize)
                    {
                        logger.LogDebug($"Пропуск сделки: недостаточно средств для минимального ордера. Нужно: {config.MinOrderSize}, доступно: {maxPosition}");
                        continue;
                    }

                    decimal riskBasedQty = (balance * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
                    decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

                    position = quantity;
                    entryPrice = (decimal)currentPrice;

                    decimal entryCommission = quantity * entryPrice * config.CommissionPercent / 100m;
                    totalCommission += entryCommission;
                    balance -= entryCommission;

                    tradeHistory.Add(new TradeRecord(
                        currentKline.OpenTime,
                        useMachineLearning ? "BUY (ML)" : "BUY",
                        quantity,
                        entryPrice,
                        0,
                        entryPrice - safeAtr * config.AtrMultiplierSL,
                        entryPrice + safeAtr * config.AtrMultiplierTP,
                        -entryCommission,
                        entryCommission,
                        $"Открытие длинной позиции. ATR: {safeAtr:F2}"));
                }
                else if (isBearish && position >= 0)
                {
                    signalsGenerated++;

                    if (position > 0)
                    {
                        decimal pnl = position * ((decimal)currentPrice - entryPrice);
                        decimal commission = position * (decimal)currentPrice * config.CommissionPercent / 100m;
                        totalCommission += commission;
                        pnl -= commission;

                        balance += pnl;
                        tradesExecuted++;
                        if (pnl > 0) profitableTrades++;

                        tradeHistory.Add(new TradeRecord(
                            currentKline.OpenTime,
                            "SELL (Close)",
                            position,
                            entryPrice,
                            (decimal)currentPrice,
                            0, 0, pnl,
                            commission,
                            "Закрытие длинной позиции перед открытием короткой"));
                    }

                    decimal maxPosition = balance * config.MaxPositionSizePercent / (decimal)currentPrice;

                    if (maxPosition < config.MinOrderSize)
                    {
                        logger.LogDebug($"Пропуск сделки: недостаточно средств для минимального ордера. Нужно: {config.MinOrderSize}, доступно: {maxPosition}");
                        continue;
                    }

                    decimal riskBasedQty = (balance * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
                    decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

                    position = -quantity;
                    entryPrice = (decimal)currentPrice;

                    decimal entryCommission = quantity * entryPrice * config.CommissionPercent / 100m;
                    totalCommission += entryCommission;
                    balance -= entryCommission;

                    tradeHistory.Add(new TradeRecord(
                        currentKline.OpenTime,
                        useMachineLearning ? "SELL (ML)" : "SELL",
                        quantity,
                        entryPrice,
                        0,
                        entryPrice + safeAtr * config.AtrMultiplierSL,
                        entryPrice - safeAtr * config.AtrMultiplierTP,
                        -entryCommission,
                        entryCommission,
                        $"Открытие короткой позиции. ATR: {safeAtr:F2}"));
                }

                equityCurve.Add(balance + position * ((decimal)currentPrice - entryPrice));
            }

            if (position != 0)
            {
                var lastPrice = (double)allKlines.Last().ClosePrice;
                decimal pnl = position > 0
                    ? position * ((decimal)lastPrice - entryPrice)
                    : position * (entryPrice - (decimal)lastPrice);

                decimal commission = Math.Abs(position) * (decimal)lastPrice * config.CommissionPercent / 100m;
                totalCommission += commission;
                pnl -= commission;

                balance += pnl;
                tradesExecuted++;
                if (pnl > 0) profitableTrades++;

                tradeHistory.Add(new TradeRecord(
                    allKlines.Last().OpenTime,
                    position > 0 ? "SELL (Close)" : "BUY (Close)",
                    Math.Abs(position),
                    entryPrice,
                    (decimal)lastPrice,
                    0, 0, pnl,
                    commission,
                    "Принудительное закрытие позиции в конце теста"));
            }

            decimal profit = balance - config.InitialBalance;
            decimal profitPercentage = (balance / config.InitialBalance - 1) * 100;
            int totalTrades = tradeHistory.Count(t => t.IsClosed);
            decimal winRate = totalTrades > 0 ?
                tradeHistory.Count(t => t.IsClosed && t.PnL > 0) * 100m / totalTrades : 0;
            decimal sharpeRatio = (decimal)CalculateSharpeRatio(equityCurve);
            decimal maxDrawdownPercent = (decimal)CalculateMaxDrawdown(equityCurve);

            logger.LogInformation("\n=== РЕЗУЛЬТАТЫ БЭКТЕСТА ===");
            logger.LogInformation($"Сигналов сгенерировано: {signalsGenerated}");
            if (useMachineLearning)
            {
                logger.LogInformation($"Подтверждено ML: {mlConfirmations}");
                logger.LogInformation($"Отклонено ML: {mlRejections}");
            }
            logger.LogInformation($"Сделок выполнено: {tradesExecuted}");
            logger.LogInformation($"Конечный баланс: {balance:F2}");
            logger.LogInformation($"Прибыль: {profit:F2} ({profitPercentage:F2}%)");
            logger.LogInformation($"Комиссии: {totalCommission:F2}");
            logger.LogInformation($"Процент прибыльных сделок: {winRate:F2}%");
            logger.LogInformation($"Максимальная просадка: {maxDrawdownPercent:F2}%");
            logger.LogInformation($"Коэффициент Шарпа: {sharpeRatio:F2}");

            var message = new StringBuilder();
            message.AppendLine($"📊 Результаты бэктеста {description}: {config.Symbol}");
            message.AppendLine($"Период: {config.BacktestStartDate:dd.MM.yyyy} - {config.BacktestEndDate:dd.MM.yyyy}");
            message.AppendLine($"Таймфрейм: {config.BacktestInterval}");
            message.AppendLine($"Баланс: {config.InitialBalance:F2} → {balance:F2}");
            message.AppendLine($"Прибыль: {profit:F2} ({profitPercentage:F2}%)");
            message.AppendLine($"Комиссии: {totalCommission:F2}");
            message.AppendLine($"Сделок: {totalTrades} | Прибыльных: {winRate:F2}%");
            message.AppendLine($"Просадка: {maxDrawdownPercent:F2}%");
            message.AppendLine($"Шарп: {sharpeRatio:F2}");

            if (useMachineLearning)
            {
                message.AppendLine($"ML подтвердил: {mlConfirmations} | Отклонил: {mlRejections}");
            }

            await telegramBot.SendMessage(config.TelegramChatId, message.ToString());
            SaveTradeHistory(tradeHistory, useMachineLearning ? "BacktestWithML" : "Backtest");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка в RunBacktestUniversal");
            await telegramBot.SendMessage(config.TelegramChatId,
                $"❌ Ошибка при выполнении бэктеста: {ex.Message}");
        }
    }

    private static async Task RunLiveTrading(BinanceRestClient binanceClient, TelegramBotClient telegramBot)
    {
        logger.LogInformation("Запуск режима реальной торговли...");
        var trailingStopState = new TrailingStopState
        {
            ActivationPrice = 0,
            StopLevel = 0,
            IsActive = false
        };

        try
        {
            if (config.UseMachineLearning &&
                (DateTime.Now - lastModelTrainingTime).TotalHours >= config.MlTrainingIntervalHours)
            {
                logger.LogInformation("Переобучение модели ML...");
                await TrainInitialModel(binanceClient);
            }

            while (true)
            {
                try
                {
                    if (!IsOptimalTradingTime())
                    {
                        logger.LogInformation("Вне оптимального времени торговли. Пропуск.");
                        await Task.Delay(TimeSpan.FromMinutes(config.CheckIntervalMinutes));
                        continue;
                    }

                    await CheckMarketAndTradeAsync(binanceClient, telegramBot, trailingStopState);
                    await Task.Delay(TimeSpan.FromMinutes(config.CheckIntervalMinutes));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ошибка в торговом цикле");
                    await telegramBot.SendMessage(config.TelegramChatId, $"⚠️ Ошибка в торговом цикле: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(30));
                    trailingStopState.IsActive = false;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Критическая ошибка в основном цикле торговли");
            await telegramBot.SendMessage(config.TelegramChatId, $"❌ Критическая ошибка: {ex.Message}");
        }
        finally
        {
            logger.LogInformation("Режим реальной торговли остановлен");
        }
    }

    private class TrailingStopState
    {
        public decimal ActivationPrice { get; set; }
        public decimal StopLevel { get; set; }
        public bool IsActive { get; set; }
    }

    private static async Task CheckMarketAndTradeAsync(
         BinanceRestClient binanceClient,
         TelegramBotClient telegramBot,
         TrailingStopState trailingStopState)
    {
        if (DateTime.Now.Date != lastTradeDate.Date)
        {
            dailyPnL = 0;
            lastTradeDate = DateTime.Now.Date;
            logger.LogInformation("Новый торговый день. Сброс дневного PnL.");
        }

        if (dailyPnL <= -config.InitialBalance * config.MaxDailyLossPercent)
        {
            logger.LogWarning("Достигнут дневной лимит убытков. Торговля приостановлена до следующего дня.");
            return;
        }

        var primaryKlines = await GetKlinesForTimeframe(binanceClient, config.PrimaryTimeframe);
        var higherKlines = await GetKlinesForTimeframe(binanceClient, config.HigherTimeframe);
        var lowerKlines = await GetKlinesForTimeframe(binanceClient, config.LowerTimeframe);

        if (primaryKlines == null || higherKlines == null || lowerKlines == null)
        {
            logger.LogError("Не удалось получить данные с одного из таймфреймов");
            return;
        }

        if (!CheckLiveVolumeFilter(primaryKlines))
        {
            logger.LogDebug("Фильтр объема не пройден");
            return;
        }

        if (!CheckLiveVolatilityFilter(primaryKlines))
        {
            logger.LogDebug("Фильтр волатильности не пройден");
            return;
        }

        var (supertrendValues, supertrendDirections) = CalculateSupertrend(
            primaryKlines,
            config.SupertrendPeriod,
            config.SupertrendMultiplier);
        bool isSupertrendBullish = supertrendDirections.LastOrDefault();

        var atrPeriod = (int)Math.Ceiling(24 / (decimal)config.PrimaryTimeframe.ToTimeSpan().TotalHours);
        var atr = CalculateATR(primaryKlines.TakeLast(atrPeriod).ToList(), atrPeriod);
        var safeAtr = atr > 0 ? atr : primaryKlines.Last().ClosePrice * config.MinAtrPercent;

        var ticker = await binanceClient.SpotApi.ExchangeData.GetPriceAsync(config.Symbol);
        if (!ticker.Success)
        {
            logger.LogError("Ошибка получения цены: {Error}", ticker.Error);
            return;
        }
        var currentPrice = ticker.Data.Price;

        var (isBullish, isBearish) = GenerateSignals(
            primaryKlines,
            higherKlines,
            lowerKlines,
            safeAtr,
            currentPrice,
            isSupertrendBullish);

        logger.LogInformation(
            "{Time} | Цена: {Price} | EMA{fastPeriod}: {FastMA} | EMA{slowPeriod}: {SlowMA} | SMI: {SMI}/{Signal} | VWMA: {VWMA} | WRSI: {WRSI} | ADXVMA: {ADXVMA} | ATR: {ATR} | ADX: {ADX} | Supertrend: {Supertrend}",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            currentPrice.ToString("F2"),
            config.FastMAPeriod,
            CalculateEma(primaryKlines.Select(k => (double)k.ClosePrice).ToArray(), config.FastMAPeriod).ToString("F2"),
            config.SlowMAPeriod,
            CalculateEma(primaryKlines.Select(k => (double)k.ClosePrice).ToArray(), config.SlowMAPeriod).ToString("F2"),
            CalculateSMI(primaryKlines.Select(k => (double)k.ClosePrice).ToArray(), 14, 3).smi.ToString("F2"),
            CalculateSMI(primaryKlines.Select(k => (double)k.ClosePrice).ToArray(), 14, 3).signal.ToString("F2"),
            CalculateVWMA(primaryKlines, 20).ToString("F2"),
            CalculateWRSI(primaryKlines.Select(k => (double)k.ClosePrice).ToArray(), config.RSIPeriod).ToString("F2"),
            CalculateADXVMA(primaryKlines.Select(k => (double)k.ClosePrice).ToArray(), primaryKlines, config.AdxPeriod, 20).ToString("F2"),
            safeAtr.ToString("F2"),
            CalculateADX(primaryKlines, config.AdxPeriod).ToString("F2"),
            isSupertrendBullish ? "Bullish" : "Bearish");

        var positions = await GetOpenPositions(binanceClient);
        var currentPosition = positions.FirstOrDefault();

        if (currentPosition != null)
        {
            if (currentPosition.Side == PositionSide.Long)
            {
                if (!trailingStopState.IsActive && currentPrice >= currentPosition.EntryPrice + safeAtr * 2)
                {
                    trailingStopState.ActivationPrice = currentPrice;
                    trailingStopState.StopLevel = trailingStopState.ActivationPrice - safeAtr;
                    trailingStopState.IsActive = true;
                    logger.LogInformation($"Активирован трейлинг-стоп для длинной позиции. Уровень: {trailingStopState.StopLevel:F2}");
                }

                if (trailingStopState.IsActive)
                {
                    if (currentPrice > trailingStopState.ActivationPrice)
                    {
                        trailingStopState.ActivationPrice = currentPrice;
                        trailingStopState.StopLevel = trailingStopState.ActivationPrice - safeAtr;
                    }

                    if (currentPrice > trailingStopState.ActivationPrice * 1.1m)
                    {
                        trailingStopState.StopLevel = currentPrice - safeAtr * 0.8m;
                        logger.LogInformation($"Обновлен уровень трейлинг-стопа: {trailingStopState.StopLevel:F2}");
                    }

                    if (currentPrice <= trailingStopState.StopLevel)
                    {
                        logger.LogInformation("Сработал трейлинг-стоп для длинной позиции");
                        await ClosePosition(binanceClient, telegramBot, currentPosition, currentPrice, "Trailing Stop");
                        trailingStopState.IsActive = false;
                        return;
                    }
                }
            }
            else
            {
                if (!trailingStopState.IsActive && currentPrice <= currentPosition.EntryPrice - safeAtr * 2)
                {
                    trailingStopState.ActivationPrice = currentPrice;
                    trailingStopState.StopLevel = trailingStopState.ActivationPrice + safeAtr;
                    trailingStopState.IsActive = true;
                    logger.LogInformation($"Активирован трейлинг-стоп для короткой позиции. Уровень: {trailingStopState.StopLevel:F2}");
                }

                if (trailingStopState.IsActive)
                {
                    if (currentPrice < trailingStopState.ActivationPrice)
                    {
                        trailingStopState.ActivationPrice = currentPrice;
                        trailingStopState.StopLevel = trailingStopState.ActivationPrice + safeAtr;
                    }

                    if (currentPrice < trailingStopState.ActivationPrice * 0.9m)
                    {
                        trailingStopState.StopLevel = currentPrice + safeAtr * 0.8m;
                        logger.LogInformation($"Обновлен уровень трейлинг-стопа: {trailingStopState.StopLevel:F2}");
                    }

                    if (currentPrice >= trailingStopState.StopLevel)
                    {
                        logger.LogInformation("Сработал трейлинг-стоп для короткой позиции");
                        await ClosePosition(binanceClient, telegramBot, currentPosition, currentPrice, "Trailing Stop");
                        trailingStopState.IsActive = false;
                        return;
                    }
                }
            }

            var lastTrade = tradeHistory.LastOrDefault();
            if (lastTrade != null && (DateTime.Now - lastTrade.Timestamp).TotalHours >= 24)
            {
                logger.LogInformation("Закрытие позиции по истечении времени");
                await ClosePosition(binanceClient, telegramBot, currentPosition, currentPrice, "Time Exit");
                return;
            }
        }

        if (currentPosition == null)
        {
            if (isBullish)
            {
                await ExecuteTradeAsync(binanceClient, telegramBot, OrderSide.Buy, currentPrice, safeAtr);
            }
            else if (isBearish)
            {
                await ExecuteTradeAsync(binanceClient, telegramBot, OrderSide.Sell, currentPrice, safeAtr);
            }
        }
    }

    private static async Task ClosePosition(
        BinanceRestClient binanceClient,
        TelegramBotClient telegramBot,
        BinancePosition position,
        decimal currentPrice,
        string reason)
    {
        var orderSide = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

        decimal executionPrice = orderSide == OrderSide.Sell ?
            currentPrice * 0.999m :
            currentPrice * 1.001m;

        var order = await binanceClient.SpotApi.Trading.PlaceOrderAsync(
            config.Symbol,
            orderSide,
            SpotOrderType.Market,
            quantity: Math.Abs(position.PositionAmount));

        if (order.Success)
        {
            decimal pnl = position.Side == PositionSide.Long
                ? position.PositionAmount * (executionPrice - position.EntryPrice)
                : position.PositionAmount * (position.EntryPrice - executionPrice);

            decimal commission = Math.Abs(position.PositionAmount) * executionPrice * config.CommissionPercent / 100m;
            dailyPnL += pnl - commission;

            var message = $"{(orderSide == OrderSide.Sell ? "🔴 ПРОДАНО" : "🟢 КУПЛЕНО")} {Math.Abs(position.PositionAmount):0.000000} {config.Symbol} по {executionPrice:0.00}\n" +
                          $"Причина: {reason}\n" +
                          $"Прибыль: {pnl:0.00} | Комиссия: {commission:0.00}";

            logger.LogInformation(message);
            await telegramBot.SendMessage(config.TelegramChatId, message);

            tradeHistory.Add(new TradeRecord(
                DateTime.Now,
                orderSide == OrderSide.Sell ? "SELL" : "BUY",
                Math.Abs(position.PositionAmount),
                position.EntryPrice,
                executionPrice,
                0, 0, pnl - commission,
                commission,
                $"Закрытие позиции. Причина: {reason}"));
        }
        else
        {
            logger.LogError("Ошибка закрытия позиции: {Error}", order.Error);
            await telegramBot.SendMessage(config.TelegramChatId,
                $"❌ Ошибка закрытия позиции: {order.Error}");
        }
    }

    private static async Task ExecuteTradeAsync(BinanceRestClient binanceClient, TelegramBotClient telegramBot, OrderSide side, decimal currentPrice, decimal atr)
    {
        if (DateTime.Now.Date != lastTradeDate.Date)
        {
            dailyPnL = 0;
            lastTradeDate = DateTime.Now.Date;
        }

        if (dailyPnL <= -config.InitialBalance * config.MaxDailyLossPercent)
        {
            logger.LogWarning("Достигнут дневной лимит убытков. Торговля приостановлена до следующего дня.");
            return;
        }

        var accountInfo = await binanceClient.SpotApi.Account.GetAccountInfoAsync();
        if (!accountInfo.Success)
        {
            logger.LogError("Ошибка получения баланса: {Error}", accountInfo.Error);
            return;
        }

        var usdtBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == "USDT")?.Available;
        if (usdtBalance is null or <= 10)
        {
            logger.LogWarning("Недостаточно USDT для торговли");
            return;
        }

        decimal availableBalance = usdtBalance.Value * 0.95m;

        var openOrders = await binanceClient.SpotApi.Trading.GetOpenOrdersAsync(config.Symbol);
        if (openOrders.Success && openOrders.Data.Any())
        {
            logger.LogInformation("Есть открытые ордера, пропускаем новую сделку");
            return;
        }

        var positions = await GetOpenPositions(binanceClient);
        if (positions.Any())
        {
            logger.LogInformation("Есть открытые позиции, пропускаем новую сделку");
            return;
        }

        decimal safeAtr = atr > 0 ? atr : currentPrice * config.MinAtrPercent;
        decimal maxPosition = availableBalance * config.MaxPositionSizePercent / currentPrice;
        decimal riskBasedQty = (availableBalance * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
        decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

        if (quantity <= config.MinOrderSize)
        {
            logger.LogWarning($"Рассчитанный объем {quantity} меньше минимального {config.MinOrderSize}");
            return;
        }

        decimal executionPrice = side == OrderSide.Buy ?
            currentPrice * 1.001m :
            currentPrice * 0.999m;

        var order = await binanceClient.SpotApi.Trading.PlaceOrderAsync(
            config.Symbol,
            side,
            SpotOrderType.Market,
            quantity: quantity);

        if (order.Success)
        {
            decimal stopLossPrice = side == OrderSide.Buy
                ? executionPrice - safeAtr * config.AtrMultiplierSL
                : executionPrice + safeAtr * config.AtrMultiplierSL;

            decimal takeProfitPrice = side == OrderSide.Buy
                ? executionPrice + safeAtr * config.AtrMultiplierTP
                : executionPrice - safeAtr * config.AtrMultiplierTP;

            decimal commission = quantity * executionPrice * config.CommissionPercent / 100m;
            dailyPnL -= commission;

            var message = $"{(side == OrderSide.Buy ? "🟢 КУПЛЕНО" : "🔴 ПРОДАНО")} {quantity:0.000000} {config.Symbol} по {executionPrice:0.00}\n" +
                          $"ATR: {safeAtr:0.00}, SL: {stopLossPrice:0.00}, TP: {takeProfitPrice:0.00}\n" +
                          $"Комиссия: {commission:0.00}";

            logger.LogInformation(message);
            await telegramBot.SendMessage(config.TelegramChatId, message);

            tradeHistory.Add(new TradeRecord(
                DateTime.Now,
                side == OrderSide.Buy ? "BUY" : "SELL",
                quantity,
                executionPrice,
                0,
                stopLossPrice,
                takeProfitPrice,
                -commission,
                commission,
                $"Открытие позиции. ATR: {safeAtr:F2}"));

            positions.Add(new BinancePosition
            {
                Symbol = config.Symbol,
                PositionAmount = side == OrderSide.Buy ? quantity : -quantity,
                EntryPrice = executionPrice,
                MarkPrice = executionPrice,
                Side = side == OrderSide.Buy ? PositionSide.Long : PositionSide.Short
            });
        }
        else
        {
            logger.LogError("Ошибка ордера: {Error}", order.Error);
            await telegramBot.SendMessage(config.TelegramChatId, $"❌ Ошибка: {order.Error}");
        }
    }

    private static async Task<List<BinancePosition>> GetOpenPositions(BinanceRestClient client)
    {
        var result = new List<BinancePosition>();

        var accountInfo = await client.SpotApi.Account.GetAccountInfoAsync();
        if (!accountInfo.Success)
        {
            logger.LogError("Ошибка получения информации об аккаунте");
            return result;
        }

        var symbolParts = config.Symbol.ToUpper().Split("USDT");
        var baseAsset = symbolParts[0];

        var baseBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == baseAsset)?.Total;
        var quoteBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == "USDT")?.Total;

        if (baseBalance > 0)
        {
            var ticker = await client.SpotApi.ExchangeData.GetPriceAsync(config.Symbol);
            if (!ticker.Success)
            {
                logger.LogError("Ошибка получения цены для расчета позиции");
                return result;
            }

            var lastTrade = tradeHistory.LastOrDefault(t => t.Type.StartsWith("BUY") && !t.IsClosed);
            decimal entryPrice = lastTrade?.EntryPrice ?? 0;

            result.Add(new BinancePosition
            {
                Symbol = config.Symbol,
                PositionAmount = baseBalance.Value,
                EntryPrice = entryPrice,
                MarkPrice = ticker.Data.Price,
                Side = PositionSide.Long
            });
        }

        return result;
    }

  

    private static double CalculateEma(double[] closes, int period, double? prevEma = null)
    {
        if (closes.Length < period)
        {
            return closes.Length > 0 ? closes.Last() : 0;
        }

        double k = 2.0 / (period + 1);
        double ema = prevEma ?? closes.Take(period).Average();

        for (int i = period; i < closes.Length; i++)
        {
            ema = closes[i] * k + ema * (1 - k);
        }

        return ema;
    }

   

    private static (double upperBand, double middleBand, double lowerBand) CalculateBollingerBands(double[] closes, int period, double stdDevMultiplier)
    {
        if (closes.Length < period)
        {
            logger.LogDebug($"Недостаточно данных для BB{period}. Нужно: {period}, есть: {closes.Length}");
            return (0, 0, 0);
        }

        var relevantCloses = closes.TakeLast(period).ToArray();
        var sma = relevantCloses.Average();
        var stdDev = Math.Sqrt(relevantCloses.Sum(x => Math.Pow(x - sma, 2)) / period);

        return (sma + stdDev * stdDevMultiplier, sma, sma - stdDev * stdDevMultiplier);
    }

    private static decimal CalculateATR(List<IBinanceKline> klines, int period)
    {
        if (klines == null || klines.Count < period)
            return 0;

        var trueRanges = new List<decimal>();

        for (int i = 1; i < klines.Count; i++)
        {
            var current = klines[i];
            var previous = klines[i - 1];

            decimal highLow = current.HighPrice - current.LowPrice;
            decimal highClose = Math.Abs(current.HighPrice - previous.ClosePrice);
            decimal lowClose = Math.Abs(current.LowPrice - previous.ClosePrice);

            trueRanges.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
        }

        if (trueRanges.Count < period)
            return 0;

        decimal atr = trueRanges.Take(period).Average();
        for (int i = period; i < trueRanges.Count; i++)
        {
            atr = (atr * (period - 1) + trueRanges[i]) / period;
        }

        return atr;
    }

    private static double CalculateADX(List<IBinanceKline> klines, int period)
    {
        if (klines == null || klines.Count < period * 2)
        {
            logger.LogDebug($"Недостаточно данных для ADX{period}. Нужно: {period * 2}, есть: {klines?.Count ?? 0}");
            return 0;
        }

        try
        {
            var positiveDMs = new List<decimal>();
            var negativeDMs = new List<decimal>();
            var trueRanges = new List<decimal>();

            for (int i = 1; i < klines.Count; i++)
            {
                var current = klines[i];
                var previous = klines[i - 1];

                decimal upMove = current.HighPrice - previous.HighPrice;
                decimal downMove = previous.LowPrice - current.LowPrice;
                decimal trueRange = Math.Max(
                    Math.Max(
                        current.HighPrice - current.LowPrice,
                        Math.Abs(current.HighPrice - previous.ClosePrice)
                    ),
                    Math.Abs(current.LowPrice - previous.ClosePrice)
                );

                trueRanges.Add(trueRange);

                if (upMove > downMove && upMove > 0)
                {
                    positiveDMs.Add(upMove);
                    negativeDMs.Add(0);
                }
                else if (downMove > upMove && downMove > 0)
                {
                    positiveDMs.Add(0);
                    negativeDMs.Add(downMove);
                }
                else
                {
                    positiveDMs.Add(0);
                    negativeDMs.Add(0);
                }
            }

            decimal smoothTR = trueRanges.Take(period).Sum();
            decimal smoothPlusDM = positiveDMs.Take(period).Sum();
            decimal smoothMinusDM = negativeDMs.Take(period).Sum();

            var plusDIs = new List<decimal>();
            var minusDIs = new List<decimal>();
            var dxValues = new List<decimal>();

            for (int i = period; i < trueRanges.Count; i++)
            {
                smoothTR = smoothTR - (smoothTR / period) + trueRanges[i];
                smoothPlusDM = smoothPlusDM - (smoothPlusDM / period) + positiveDMs[i];
                smoothMinusDM = smoothMinusDM - (smoothMinusDM / period) + negativeDMs[i];

                decimal plusDI = (smoothPlusDM / smoothTR) * 100;
                decimal minusDI = (smoothMinusDM / smoothTR) * 100;
                decimal dx = Math.Abs(plusDI - minusDI) / (plusDI + minusDI) * 100;

                plusDIs.Add(plusDI);
                minusDIs.Add(minusDI);
                dxValues.Add(dx);
            }

            if (dxValues.Count < period)
                return 0;

            decimal adx = dxValues.Take(period).Average();
            for (int i = period; i < dxValues.Count; i++)
            {
                adx = ((adx * (period - 1)) + dxValues[i]) / period;
            }

            return (double)adx;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка расчета ADX");
            return 0;
        }
    }

   

    private static async Task<List<IBinanceKline>> GetAllHistoricalData(BinanceRestClient binanceClient)
    {
        var allKlines = new List<IBinanceKline>();
        var currentStartTime = config.BacktestStartDate;

        try
        {
            logger.LogInformation($"Загрузка данных с {config.BacktestStartDate} по {config.BacktestEndDate}");

            while (currentStartTime < config.BacktestEndDate)
            {
                var klinesResult = await binanceClient.SpotApi.ExchangeData.GetKlinesAsync(
                    config.Symbol,
                    config.BacktestInterval,
                    startTime: currentStartTime,
                    endTime: config.BacktestEndDate,
                    limit: 1000);

                if (!klinesResult.Success)
                {
                    logger.LogError("Ошибка получения данных: {Error}", klinesResult.Error);
                    return null;
                }

                if (!klinesResult.Data.Any())
                    break;

                allKlines.AddRange(klinesResult.Data);
                currentStartTime = klinesResult.Data.Last().OpenTime.AddMinutes(1);

                await Task.Delay(250);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при получении исторических данных");
            return null;
        }

        logger.LogInformation($"Получено {allKlines.Count} свечей");
        return allKlines.Count > 0 ? allKlines : null;
    }

    private static bool CheckVolumeFilter(List<IBinanceKline> klines, int currentIndex)
    {
        if (klines == null || klines.Count == 0 || currentIndex < 0 || currentIndex >= klines.Count)
            return false;

        if (currentIndex < 2)
            return true;

        var currentKline = klines[currentIndex - 1];
        var prevKline = klines[currentIndex - 2];

        if (currentKline == null || prevKline == null)
            return false;

        decimal currentVolumeUSDT = currentKline.Volume * currentKline.ClosePrice;
        if (currentVolumeUSDT < config.MinVolumeUSDT)
        {
            logger.LogDebug($"Фильтр объема не пройден: {currentVolumeUSDT} < {config.MinVolumeUSDT}");
            return false;
        }

        if (prevKline.Volume == 0)
            return true;

        var volumeChange = Math.Abs((currentKline.Volume - prevKline.Volume) / prevKline.Volume);
        bool passed = volumeChange >= config.VolumeChangeThreshold;

        if (!passed)
            logger.LogDebug($"Фильтр изменения объема не пройден: {volumeChange:P2} < {config.VolumeChangeThreshold:P2}");

        return passed;
    }

    private static bool CheckVolatilityFilter(List<IBinanceKline> klines, int currentIndex)
    {
        if (currentIndex < config.VolatilityPeriod)
        {
            logger.LogDebug("Недостаточно данных для проверки волатильности");
            return true;
        }

        var relevantKlines = klines.Skip(currentIndex - config.VolatilityPeriod).Take(config.VolatilityPeriod).ToList();
        var atr = CalculateATR(relevantKlines, config.VolatilityPeriod);

        var currentPrice = klines[currentIndex].ClosePrice;
        var volatility = atr / currentPrice;

        bool passed = volatility >= config.VolatilityThreshold;

        if (!passed)
            logger.LogDebug($"Фильтр волатильности не пройден: {volatility:P2} < {config.VolatilityThreshold:P2}");

        return passed;
    }

    private static bool CheckLiveVolumeFilter(List<IBinanceKline> klines)
    {
        if (klines.Count < 2)
        {
            logger.LogDebug("Недостаточно данных для проверки объема");
            return false;
        }

        var currentKline = klines.Last();
        var prevKline = klines[^2];

        decimal currentVolumeUSDT = currentKline.Volume * currentKline.ClosePrice;
        if (currentVolumeUSDT < config.MinVolumeUSDT)
        {
            logger.LogDebug($"Фильтр ликвидности: {currentVolumeUSDT} < {config.MinVolumeUSDT}");
            return false;
        }

        if (prevKline.Volume == 0)
            return true;

        var volumeChange = Math.Abs((currentKline.Volume - prevKline.Volume) / prevKline.Volume);
        bool passed = volumeChange >= config.VolumeChangeThreshold;

        if (!passed)
            logger.LogDebug($"Фильтр изменения объема не пройден: {volumeChange:P2} < {config.VolumeChangeThreshold:P2}");

        return passed;
    }

    private static bool CheckLiveVolatilityFilter(List<IBinanceKline> klines)
    {
        if (klines.Count < config.VolatilityPeriod)
        {
            logger.LogDebug("Недостаточно данных для проверки волатильности");
            return false;
        }

        var relevantKlines = klines.TakeLast(config.VolatilityPeriod).ToList();
        var atr = CalculateATR(relevantKlines, config.VolatilityPeriod);

        var currentPrice = klines.Last().ClosePrice;
        var volatility = atr / currentPrice;

        bool passed = volatility >= config.VolatilityThreshold;

        if (!passed)
            logger.LogDebug($"Фильтр волатильности не пройден: {volatility:P2} < {config.VolatilityThreshold:P2}");

        return passed;
    }

    private static async Task<List<IBinanceKline>> GetKlinesForTimeframe(BinanceRestClient client, KlineInterval timeframe)
    {
        int requiredBars = Math.Max(Math.Max(config.SlowMAPeriod, config.RSIPeriod), config.BbPeriod) + 50;

        var klinesResult = await client.SpotApi.ExchangeData.GetKlinesAsync(
            config.Symbol,
            timeframe,
            limit: requiredBars);

        if (!klinesResult.Success)
        {
            logger.LogError("Ошибка получения свечей для таймфрейма {0}: {1}", timeframe, klinesResult.Error);
            return null;
        }

        return klinesResult.Data.ToList();
    }

    private static double CalculateSharpeRatio(List<decimal> equityCurve, bool isHourly = false)
    {
        if (equityCurve == null || equityCurve.Count < 2) return 0;

        try
        {
            if (equityCurve.Count < 2) return 0;

            var returns = new List<double>();
            for (int i = 1; i < equityCurve.Count; i++)
            {
                double ret = (double)((equityCurve[i] - equityCurve[i - 1]) / equityCurve[i - 1]);
                returns.Add(ret);
            }

            if (!returns.Any()) return 0;

            double avgReturn = returns.Average();
            double stdDev = Math.Sqrt(returns.Sum(r => Math.Pow(r - avgReturn, 2)) / returns.Count);

            if (stdDev == 0) return 0;

            double riskFreeRate = 0.05 / 100;
            double annualizationFactor = isHourly ? Math.Sqrt(365 * 24) : Math.Sqrt(365);
            return (avgReturn - riskFreeRate) / stdDev * annualizationFactor;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка расчета коэффициента Шарпа");
            return 0;
        }
    }

    private static double CalculateMaxDrawdown(List<decimal> equityCurve)
    {
        if (equityCurve.Count == 0) return 0;

        decimal peak = equityCurve[0];
        decimal maxDrawdown = 0;

        foreach (var value in equityCurve)
        {
            if (value > peak) peak = value;
            decimal drawdown = (peak - value) / peak * 100;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
        }

        return (double)maxDrawdown;
    }

    private static TradingParams MutateParams(TradingParams bestParams, Random random, double mutationStrength)
    {
        return new TradingParams(
            MutateValue(bestParams.FastMAPeriod, config.FastMAPeriodRange[0], config.FastMAPeriodRange[1], random, mutationStrength),
            MutateValue(bestParams.SlowMAPeriod, config.SlowMAPeriodRange[0], config.SlowMAPeriodRange[1], random, mutationStrength),
            MutateValue(bestParams.RSIPeriod, config.RSIPeriodRange[0], config.RSIPeriodRange[1], random, mutationStrength),
            MutateValue(bestParams.OverboughtLevel, config.OverboughtLevelRange[0], config.OverboughtLevelRange[1], random, mutationStrength),
            MutateValue(bestParams.OversoldLevel, config.OversoldLevelRange[0], config.OversoldLevelRange[1], random, mutationStrength),
            MutateValue(bestParams.BbPeriod, 10, 30, random, mutationStrength),
            Math.Max(1.0, Math.Min(3.0, bestParams.BbStdDev + (random.NextDouble() - 0.5) * 0.5 * mutationStrength)));
    }

    private static T MutateValue<T>(T value, T min, T max, Random random, double mutationStrength) where T : struct
    {
        if (random.NextDouble() >= 0.3 * mutationStrength)
            return value;

        if (typeof(T) == typeof(int))
        {
            int val = (int)(object)value;
            int minVal = (int)(object)min;
            int maxVal = (int)(object)max;
            int change = random.Next(-1, 2) * (int)Math.Ceiling(mutationStrength * 2);
            int newValue = val + change;
            return (T)(object)Math.Min(maxVal, Math.Max(minVal, newValue));
        }
        else if (typeof(T) == typeof(double))
        {
            double val = (double)(object)value;
            double minVal = (double)(object)min;
            double maxVal = (double)(object)max;
            double change = (random.NextDouble() - 0.5) * (maxVal - minVal) * 0.1 * mutationStrength;
            double newValue = val + change;
            return (T)(object)Math.Min(maxVal, Math.Max(minVal, newValue));
        }

        throw new NotSupportedException($"Тип {typeof(T)} не поддерживается");
    }

    private static void SaveTradeHistory(List<TradeRecord> history, string prefix = "TradeHistory")
    {
        try
        {
            string fileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            using var writer = new StreamWriter(fileName);

            writer.WriteLine("Timestamp,Type,Quantity,EntryPrice,ExitPrice,StopLoss,TakeProfit,PnL,Commission,Notes");

            foreach (var trade in history)
            {
                writer.WriteLine($"{trade.Timestamp:yyyy-MM-dd HH:mm:ss},{trade.Type}," +
                                $"{trade.Quantity:F6},{trade.EntryPrice:F2},{trade.ExitPrice:F2}," +
                                $"{trade.StopLossPrice:F2},{trade.TakeProfitPrice:F2},{trade.PnL:F2}," +
                                $"{trade.Commission:F2},\"{trade.Notes}\"");
            }

            logger.LogInformation("История сделок сохранена в файл: {FileName}", fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при сохранении истории сделок");
        }
    }

    private static DateTime lastFearGreedUpdate = DateTime.MinValue;
    private static float cachedFearGreedValue = 50f;

    private static async Task<float> GetFearGreedIndex()
    {
        if (config.BacktestMode)
        {
            return 50f;
        }

        if (DateTime.Now - lastFearGreedUpdate < TimeSpan.FromMinutes(1))
        {
            return cachedFearGreedValue;
        }

        try
        {
            var response = await httpClient.GetAsync(config.FearGreedIndexUrl);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                logger.LogDebug("Raw JSON response: {json}", json);

                using var doc = JsonDocument.Parse(json);

                var metadata = doc.RootElement.GetProperty("metadata");
                if (metadata.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    string errorMsg = error.GetString();
                    logger.LogWarning("API вернуло ошибку: {error}", errorMsg);
                    return cachedFearGreedValue;
                }

                var dataArray = doc.RootElement.GetProperty("data");

                if (dataArray.ValueKind == JsonValueKind.Array && dataArray.GetArrayLength() > 0)
                {
                    var firstItem = dataArray[0];
                    if (firstItem.TryGetProperty("value", out var valueProp))
                    {
                        string valueString = valueProp.GetString();

                        if (int.TryParse(valueString, out int value))
                        {
                            lastFearGreedUpdate = DateTime.Now;
                            cachedFearGreedValue = value;
                            return value;
                        }

                        logger.LogError("Не удалось преобразовать значение индекса: {valueString}", valueString);
                    }
                }
                else
                {
                    logger.LogWarning("Пустой массив данных в ответе API");
                }
            }
            else
            {
                logger.LogError("Ошибка HTTP: {statusCode} {reason}",
                    (int)response.StatusCode, response.ReasonPhrase);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка получения индекса страха и жадности");
        }

        return cachedFearGreedValue;
    }

    private static async Task<float> CalculateSP500Correlation(double[] btcPrices)
    {
        return new Random().Next(70, 130) / 100f;
    }

    private static async Task<float> GetFundingRate()
    {
        return new Random().Next(-50, 50) / 10000f;
    }

    public class BinancePosition
    {
        public string Symbol { get; set; }
        public decimal PositionAmount { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public PositionSide Side { get; set; }
    }

    public enum PositionSide
    {
        Long,
        Short
    }
}

public static class KlineIntervalExtensions
{
    public static TimeSpan ToTimeSpan(this KlineInterval interval)
    {
        return interval switch
        {
            KlineInterval.OneMinute => TimeSpan.FromMinutes(1),
            KlineInterval.ThreeMinutes => TimeSpan.FromMinutes(3),
            KlineInterval.FiveMinutes => TimeSpan.FromMinutes(5),
            KlineInterval.FifteenMinutes => TimeSpan.FromMinutes(15),
            KlineInterval.ThirtyMinutes => TimeSpan.FromMinutes(30),
            KlineInterval.OneHour => TimeSpan.FromHours(1),
            KlineInterval.TwoHour => TimeSpan.FromHours(2),
            KlineInterval.FourHour => TimeSpan.FromHours(4),
            KlineInterval.OneDay => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1)
        };
    }
}