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
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;

public class Program
{
    // Конфигурация бота с обновленными параметрами
    public class BotConfig
    {
        // API ключи
        public string ApiKey { get; set; } = "YOUR_BINANCE_API_KEY";
        public string ApiSecret { get; set; } = "YOUR_BINANCE_API_SECRET";

        // Настройки Telegram
        public string TelegramToken { get; set; } = "6299377057:AAHaNlY93hdrdQVanTPgmMibgQt41UDidRA";
        public string TelegramChatId { get; set; } = "1314937104";
        public string Symbol { get; set; } = "BTCUSDT";

        // Управление рисками
        public decimal RiskPerTrade { get; set; } = 0.05m; // Риск 2% от баланса на сделку
        public decimal MaxDailyLossPercent { get; set; } = 0.10m; // Макс дневная просадка 10%
        public decimal AtrMultiplierSL { get; set; } = 1.5m; // Множитель ATR для стоп-лосса
        public decimal AtrMultiplierTP { get; set; } = 3.0m; // Множитель ATR для тейк-профита
        public decimal MinAtrPercent { get; set; } = 0.01m; // Минимальное значение ATR (1% от цены)
        public decimal MinOrderSize { get; set; } = 0.0001m; // Минимальный размер ордера BTC
        public decimal MaxPositionSizePercent { get; set; } = 0.1m; // Макс размер позиции 10% от баланса

        // Диапазоны параметров для оптимизации
        public int[] FastMAPeriodRange { get; set; } = new[] { 5, 50 };
        public int[] SlowMAPeriodRange { get; set; } = new[] { 15, 100 };
        public int[] RSIPeriodRange { get; set; } = new[] { 10, 30 };
        public double[] OverboughtLevelRange { get; set; } = new[] { 60.0, 80.0 };
        public double[] OversoldLevelRange { get; set; } = new[] { 20.0, 40.0 };

        // Фильтры рынка
        public decimal MinVolumeUSDT { get; set; } = 1000000m; // Минимальный объем в USDT
        public decimal VolumeChangeThreshold { get; set; } = 0.5m; // Порог изменения объема
        public decimal VolatilityThreshold { get; set; } = 0.02m; // Минимальная волатильность
        public int VolatilityPeriod { get; set; } = 14; // Период для расчета волатильности

        // Таймфреймы для анализа (основной изменен на 4 часа)
        public KlineInterval PrimaryTimeframe { get; set; } = KlineInterval.FourHour;
        public KlineInterval HigherTimeframe { get; set; } = KlineInterval.OneDay;
        public KlineInterval LowerTimeframe { get; set; } = KlineInterval.OneHour;

        // Параметры индикаторов по умолчанию
        public int FastMAPeriod { get; set; } = 12;
        public int SlowMAPeriod { get; set; } = 26;
        public int RSIPeriod { get; set; } = 14;
        public double OverboughtLevel { get; set; } = 70.0;
        public double OversoldLevel { get; set; } = 30.0;
        public int BbPeriod { get; set; } = 20;
        public double BbStdDev { get; set; } = 2.0;
        public int FastEmaPeriod { get; set; } = 12;
        public int SlowEmaPeriod { get; set; } = 26;
        public int SignalPeriod { get; set; } = 9;

        // Настройки машинного обучения (ужесточены)
        public int MlLookbackPeriod { get; set; } = 100;
        public int MlPredictionHorizon { get; set; } = 10; // Увеличен горизонт
        public double MlConfidenceThreshold { get; set; } = 0.7; // Более строгий порог
        public bool UseMachineLearning { get; set; } = true;
        public int MlTrainingIntervalHours { get; set; } = 24;
        public int MlWindowSize { get; set; } = 200;
        public double MlPositiveClassWeight { get; set; } = 0.7;
        public int MlMinTrainingSamples { get; set; } = 1000;

        // Настройки оптимизации (скорректированы)
        public int OptimizationGenerations { get; set; } = 10;
        public int OptimizationPopulationSize { get; set; } = 50;
        public int CheckIntervalMinutes { get; set; } = 5;
        public double OptimizationMinWinRate { get; set; } = 0.45; // Минимальный процент прибыльных сделок
        public double OptimizationMinProfitRatio { get; set; } = 0.8; // Снижен до 90%
        public double OptimizationSharpeWeight { get; set; } = 0.4; // Вес коэффициента Шарпа
        public double OptimizationProfitWeight { get; set; } = 0.3; // Вес коэффициента прибыльности
        public double OptimizationWinRateWeight { get; set; } = 0.2; // Вес процента прибыльных сделок
        public double OptimizationDrawdownWeight { get; set; } = 0.1; // Вес максимальной просадки

        // Режимы работы бота
        public bool BacktestMode { get; set; } = true;
        public bool OptimizeMode { get; set; } = true;
        public bool LiveTradeMode { get; set; } = false;
        public DateTime BacktestStartDate { get; set; } = new DateTime(2025, 1, 1);
        public DateTime BacktestEndDate { get; set; } = DateTime.Now;
        public KlineInterval BacktestInterval { get; set; } = KlineInterval.FourHour; // Изменен на 4 часа
        public decimal InitialBalance { get; set; } = 1000m;
        public decimal CommissionPercent { get; set; } = 0.04m; // Реальные комиссии Binance
    }

    // Параметры торговой стратегии
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

    // Запись о сделке
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

    // Данные для машинного обучения
    public class MarketData
    {
        [LoadColumn(0)] public float Open { get; set; }
        [LoadColumn(1)] public float High { get; set; }
        [LoadColumn(2)] public float Low { get; set; }
        [LoadColumn(3)] public float Close { get; set; }
        [LoadColumn(4)] public float Volume { get; set; }
        [LoadColumn(5)] public float RSI { get; set; }
        [LoadColumn(6)] public float MACD { get; set; }
        [LoadColumn(7)] public float MACDSignal { get; set; }
        [LoadColumn(8)] public float SMA5 { get; set; }
        [LoadColumn(9)] public float SMA20 { get; set; }
        [LoadColumn(10)] public float BBUpper { get; set; }
        [LoadColumn(11)] public float BBLower { get; set; }
        [LoadColumn(12)] public float ATR { get; set; }
        [LoadColumn(13)] public float VolumeChange { get; set; }
        [LoadColumn(14)] public float HigherTrend { get; set; }
        [LoadColumn(15)] public float MarketSentiment { get; set; }
        [ColumnName("Label")]
        public bool Target { get; set; }
    }

    // Предсказание модели
    public class MarketPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool PredictedLabel { get; set; }

        [ColumnName("Score")]
        public float Score { get; set; }

        public bool ConfirmedPrediction => PredictedLabel && Score >= (float)config.MlConfidenceThreshold;
    }

    // Глобальные переменные
    private static BotConfig config = new BotConfig();
    private static ILogger logger;
    private static decimal dailyPnL = 0;
    private static DateTime lastTradeDate = DateTime.MinValue;
    private static MLContext mlContext = new MLContext();
    private static ITransformer mlModel;
    private static DateTime lastModelTrainingTime = DateTime.MinValue;
    private static List<TradeRecord> tradeHistory = new List<TradeRecord>();
    private static Dictionary<DateTime, decimal> dailyBalances = new Dictionary<DateTime, decimal>();

    // Основной метод
    public static async Task Main(string[] args)
    {
        // Настройка логгера
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddFile("logs/bot_{Date}.log");
        });

        logger = loggerFactory.CreateLogger("CryptoBot");
        var telegramBot = new TelegramBotClient(config.TelegramToken);

        try
        {
            // Инициализация клиента Binance
            var binanceClient = new BinanceRestClient(options =>
            {
                options.ApiCredentials = new ApiCredentials(config.ApiKey, config.ApiSecret);
            });

            // Режим бэктеста
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

            // Режим оптимизации
            if (config.OptimizeMode)
            {
                await OptimizeParameters(binanceClient, telegramBot);
            }

            // Бэктест с оптимизированными параметрами
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

            // Режим реальной торговли
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

    // Инициализация модели машинного обучения
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

            var mlData = PrepareMLData(historicalData);
            if (mlData.Count == 0)
            {
                logger.LogError("Не удалось подготовить данные для обучения модели");
                return;
            }

            TrainModel(mlData);
            lastModelTrainingTime = DateTime.Now;
            logger.LogInformation("Начальная модель машинного обучения успешно обучена");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при обучении начальной модели");
        }
    }

    // Подготовка данных для ML с устранением утечки данных
    private static List<MarketData> PrepareMLData(List<IBinanceKline> klines)
    {
        var mlData = new List<MarketData>();
        if (klines == null || klines.Count < config.MlLookbackPeriod + config.MlPredictionHorizon)
        {
            logger.LogWarning($"Недостаточно данных для ML. Нужно: {config.MlLookbackPeriod + config.MlPredictionHorizon}, есть: {klines.Count}");
            return mlData;
        }

        int positiveCount = 0;
        int negativeCount = 0;

        // Получаем данные старшего таймфрейма
        var higherTimeframeKlines = AggregateKlinesToHigherTimeframe(klines, config.HigherTimeframe);

        // Устранение утечки данных: используем только прошлые данные
        for (int i = config.MlLookbackPeriod; i < klines.Count - config.MlPredictionHorizon; i++)
        {
            // Точка предсказания смещена на горизонт предсказания назад
            int predictionPoint = i - config.MlPredictionHorizon;
            if (predictionPoint < config.MlLookbackPeriod) continue;

            var currentWindow = klines.Skip(predictionPoint - config.MlLookbackPeriod).Take(config.MlLookbackPeriod).ToList();
            var futurePrices = klines.Skip(predictionPoint).Take(config.MlPredictionHorizon).Select(k => (double)k.ClosePrice).ToArray();

            if (currentWindow.Count < config.MlLookbackPeriod || futurePrices.Length < config.MlPredictionHorizon)
                continue;

            // Расчет индикаторов
            var closes = currentWindow.Select(k => (double)k.ClosePrice).ToArray();
            var volumes = currentWindow.Select(k => (double)k.Volume).ToArray();
            var rsi = CalculateRsi(closes, config.RSIPeriod);
            var (macdLine, signalLine, _) = CalculateMacd(closes, config.FastMAPeriod, config.SlowMAPeriod, 9);
            var sma5 = CalculateSma(closes, 5);
            var sma20 = CalculateSma(closes, 20);
            var (upperBB, middleBB, lowerBB) = CalculateBollingerBands(closes, config.BbPeriod, config.BbStdDev);
            var atr = (float)CalculateATR(currentWindow, 14);
            var volumeChange = (float)(volumes.Last() / volumes.Take(volumes.Length - 1).Average() - 1);

            // Расчет тренда на старшем таймфрейме
            var higherCloses = higherTimeframeKlines
                .Where(k => k.OpenTime <= currentWindow.Last().OpenTime)
                .Take(config.MlLookbackPeriod / GetAggregationFactor(config.PrimaryTimeframe, config.HigherTimeframe))
                .Select(k => (double)k.ClosePrice)
                .ToArray();

            var higherFastMa = CalculateSma(higherCloses, config.FastMAPeriod);
            var higherSlowMa = CalculateSma(higherCloses, config.SlowMAPeriod);
            float higherTrend = (float)(higherFastMa - higherSlowMa);

            // Расчет "рыночных настроений"
            float marketSentiment = (float)((closes.Last() - sma20) / (sma20 * 0.01));

            // Определение целевой переменной
            double currentPrice = closes.Last();
            double futureMaxPrice = futurePrices.Max();
            double futureMinPrice = futurePrices.Min();
            double futureAvgPrice = futurePrices.Average();

            // Условие с учетом волатильности
            double atrValue = (double)CalculateATR(currentWindow.TakeLast(14).ToList(), 14);
            bool willRise = (futureMaxPrice - currentPrice) > (currentPrice - futureMinPrice) &&
                          (futureAvgPrice - currentPrice) > atrValue * 0.5;

            // Балансировка классов
            if (willRise && positiveCount > negativeCount * config.MlPositiveClassWeight) continue;
            if (!willRise && negativeCount > positiveCount / config.MlPositiveClassWeight) continue;

            if (willRise) positiveCount++;
            else negativeCount++;

            // Добавление данных
            mlData.Add(new MarketData
            {
                Open = (float)currentWindow.Last().OpenPrice,
                High = (float)currentWindow.Last().HighPrice,
                Low = (float)currentWindow.Last().LowPrice,
                Close = (float)currentWindow.Last().ClosePrice,
                Volume = (float)currentWindow.Last().Volume,
                RSI = (float)rsi,
                MACD = (float)macdLine,
                MACDSignal = (float)signalLine,
                SMA5 = (float)sma5,
                SMA20 = (float)sma20,
                BBUpper = (float)upperBB,
                BBLower = (float)lowerBB,
                ATR = atr,
                VolumeChange = volumeChange,
                HigherTrend = higherTrend,
                MarketSentiment = marketSentiment,
                Target = willRise
            });
        }

        logger.LogInformation($"Подготовлено {mlData.Count} записей для обучения ML (Позитивные: {positiveCount}, Негативные: {negativeCount})");
        return mlData;
    }

    // Агрегация свечей на старший таймфрейм (исправленная)
    private static List<IBinanceKline> AggregateKlinesToHigherTimeframe(List<IBinanceKline> klines, KlineInterval higherTimeframe)
    {
        var result = new List<IBinanceKline>();
        if (klines.Count == 0) return result;

        // Определяем временной интервал
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

        // Добавляем последнюю группу
        if (currentGroup.Count > 0)
        {
            result.Add(CreateAggregatedKline(currentGroup, currentIntervalStart));
        }

        return result;
    }

    // Создание агрегированной свечи
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

    // Получение коэффициента агрегации
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

    // Обучение модели
    private static void TrainModel(List<MarketData> trainingData)
    {
        try
        {
            if (trainingData.Count < config.MlMinTrainingSamples)
            {
                logger.LogWarning($"Слишком мало данных для обучения: {trainingData.Count} записей (требуется {config.MlMinTrainingSamples})");
                return;
            }

            IDataView dataView = mlContext.Data.LoadFromEnumerable(trainingData);

            // Конвейер обработки данных
            var dataProcessPipeline = mlContext.Transforms
                .Concatenate("Features",
                    nameof(MarketData.Open),
                    nameof(MarketData.High),
                    nameof(MarketData.Low),
                    nameof(MarketData.Close),
                    nameof(MarketData.Volume),
                    nameof(MarketData.RSI),
                    nameof(MarketData.MACD),
                    nameof(MarketData.MACDSignal),
                    nameof(MarketData.SMA5),
                    nameof(MarketData.SMA20),
                    nameof(MarketData.BBUpper),
                    nameof(MarketData.BBLower),
                    nameof(MarketData.ATR),
                    nameof(MarketData.VolumeChange),
                    nameof(MarketData.HigherTrend),
                    nameof(MarketData.MarketSentiment))
                .Append(mlContext.Transforms.NormalizeMinMax("Features"));

            // Выбор алгоритма
            var trainer = mlContext.BinaryClassification.Trainers.LightGbm(
                labelColumnName: "Label",
                featureColumnName: "Features",
                numberOfLeaves: 31,
                minimumExampleCountPerLeaf: 10,
                learningRate: 0.05,
                numberOfIterations: 200);

            var trainingPipeline = dataProcessPipeline.Append(trainer);

            // Обучение модели
            mlModel = trainingPipeline.Fit(dataView);

            // Кросс-валидация
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

            // Сохранение модели
            mlContext.Model.Save(mlModel, dataView.Schema, "MarketPredictionModel.zip");
            logger.LogInformation("Модель сохранена в файл MarketPredictionModel.zip");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при обучении модели");
            throw;
        }
    }

    // Оптимизация параметров стратегии
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

            // Разделение данных
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

            // Генетический алгоритм
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

            // Отправка результатов
            await telegramBot.SendMessage(
                chatId: config.TelegramChatId,
                text: $"🎯 Результаты оптимизации {config.Symbol}\n" +
                      $"До оптимизации: {defaultScore:F2}\n" +
                      $"После оптимизации: {bestScore:F2}\n" +
                      $"Параметры: {bestParams}");

            // Обновление конфигурации
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

    // Оценка параметров стратегии
    private static double EvaluateParameters(List<IBinanceKline> trainKlines, List<IBinanceKline> valKlines, List<IBinanceKline> testKlines, TradingParams parameters)
    {
        try
        {
            // Тестирование на обучающей выборке
            var trainResult = BacktestWithParams(trainKlines, parameters);

            // Логирование результатов
            logger.LogInformation($"Оценка параметров {parameters} на train: " +
                                 $"Profit={trainResult.ProfitRatio:F2}, " +
                                 $"WinRate={trainResult.WinRate:F2}");

            // Проверка минимальных требований
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

            // Тестирование на валидационной выборке
            var valResult = BacktestWithParams(valKlines, parameters);

            // Тестирование на тестовой выборке
            var testResult = BacktestWithParams(testKlines, parameters);

            // Комплексная оценка с учетом стабильности
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

    // Бэктест с заданными параметрами
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

        // Получаем данные старшего таймфрейма
        var higherTimeframeKlines = AggregateKlinesToHigherTimeframe(klines, config.HigherTimeframe);

        for (int i = requiredBars; i < klines.Count; i++)
        {
            var currentKline = klines[i];
            var previousKlines = klines.Take(i).ToList();

            if (!CheckVolumeFilter(previousKlines, i)) continue;
            if (!CheckVolatilityFilter(previousKlines, i)) continue;

            var closePrices = previousKlines.Select(k => (double)k.ClosePrice).ToArray();
            var currentPrice = (double)currentKline.ClosePrice;

            // Расчет индикаторов
            var fastMa = CalculateSma(closePrices, parameters.FastMAPeriod);
            var slowMa = CalculateSma(closePrices, parameters.SlowMAPeriod);
            var rsi = CalculateRsi(closePrices, parameters.RSIPeriod);
            var (macdLine, signalLine, _) = CalculateMacd(closePrices, parameters.FastMAPeriod, parameters.SlowMAPeriod, 9);
            var (upperBand, middleBand, lowerBand) = CalculateBollingerBands(closePrices, parameters.BbPeriod, parameters.BbStdDev);

            // Расчет индикаторов для старшего таймфрейма
            var higherKlinesForTrend = higherTimeframeKlines
                .Where(k => k.OpenTime <= currentKline.OpenTime)
                .TakeLast(parameters.SlowMAPeriod / 2)
                .ToList();

            if (higherKlinesForTrend.Count < 5) continue;

            var higherCloses = higherKlinesForTrend.Select(k => (double)k.ClosePrice).ToArray();
            var higherFastMa = CalculateSma(higherCloses, parameters.FastMAPeriod / 2);
            var higherSlowMa = CalculateSma(higherCloses, parameters.SlowMAPeriod / 2);

            // Расчет ATR
            var atr = CalculateATR(previousKlines.Skip(i - 14).Take(14).ToList(), 14);
            var safeAtr = atr > 0 ? atr : (decimal)currentPrice * config.MinAtrPercent;

            // Фильтр силы тренда (добавлен)
            bool isStrongTrend = Math.Abs(higherFastMa - higherSlowMa) > (double)safeAtr * 0.5;

            // Закрытие позиции
            if (position != 0)
            {
                bool shouldClose = false;
                decimal exitPrice = 0;
                string exitReason = "";

                // Проверка времени удержания
                var holdTime = currentKline.OpenTime - tradeHistory.Last().Timestamp;
                if (holdTime.TotalHours >= 24)
                {
                    exitPrice = (decimal)currentPrice;
                    exitReason = "Time Exit";
                    shouldClose = true;
                }
                else if (position > 0) // Длинная позиция
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
                    // Трейлинг-стоп
                    else if ((decimal)currentPrice >= entryPrice + safeAtr * 2 &&
                            (decimal)currentPrice <= entryPrice + safeAtr * 2 - safeAtr * 0.5m)
                    {
                        exitPrice = (decimal)currentPrice;
                        exitReason = "Trailing Stop";
                        shouldClose = true;
                    }
                }
                else // Короткая позиция
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
                    // Трейлинг-стоп
                    else if ((decimal)currentPrice <= entryPrice - safeAtr * 2 &&
                            (decimal)currentPrice >= entryPrice - safeAtr * 2 + safeAtr * 0.5m)
                    {
                        exitPrice = (decimal)currentPrice;
                        exitReason = "Trailing Stop";
                        shouldClose = true;
                    }
                }

                if (shouldClose)
                {
                    decimal pnl = position > 0
                        ? position * (exitPrice - entryPrice)
                        : position * (entryPrice - exitPrice);

                    // Учет комиссии
                    decimal commission = Math.Abs(position) * exitPrice * config.CommissionPercent / 100m * 2;
                    totalCommission += commission;
                    pnl -= commission;

                    balance += pnl;
                    tradesCount++;
                    if (pnl > 0) profitableTrades++;

                    // Обновление просадки
                    if (balance > maxBalance) maxBalance = balance;
                    decimal drawdown = (maxBalance - balance) / maxBalance * 100;
                    if (drawdown > maxDrawdown) maxDrawdown = drawdown;

                    tradeHistory.Add(new TradeRecord(
                        currentKline.OpenTime,
                        position > 0 ? $"SELL ({exitReason})" : $"BUY ({exitReason})",
                        Math.Abs(position),
                        entryPrice,
                        exitPrice,
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

            // Улучшенные условия для входа с учетом тренда и силы тренда      
            bool isHigherTrendBullish = higherFastMa > higherSlowMa;
            bool isHigherTrendBearish = higherFastMa < higherSlowMa;

            bool isPrimaryBullish = fastMa > slowMa &&
                                  currentPrice > fastMa &&
                                  rsi < parameters.OverboughtLevel &&
                                  currentPrice > lowerBand &&
                                  macdLine > signalLine;

            bool isPrimaryBearish = fastMa < slowMa &&
                                  currentPrice < fastMa &&
                                  rsi > parameters.OversoldLevel &&
                                  currentPrice < upperBand &&
                                  macdLine < signalLine;

            // Комбинированные условия с учетом тренда и силы тренда
            bool isBullish = (isHigherTrendBullish && isPrimaryBullish && isStrongTrend);
            bool isBearish = (isHigherTrendBearish && isPrimaryBearish && isStrongTrend);

            // Вход в сделку
            if (isBullish && position <= 0)
            {
                if (position < 0) // Закрываем короткую позицию
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

                // Расчет максимально допустимого размера позиции
                decimal maxPosition = balance * config.MaxPositionSizePercent / (decimal)currentPrice;

                // Проверка возможности открытия позиции
                if (maxPosition < config.MinOrderSize)
                {
                    logger.LogDebug($"Пропуск сделки: недостаточно средств для минимального ордера. Нужно: {config.MinOrderSize}, доступно: {maxPosition}");
                    continue;
                }

                decimal riskBasedQty = (balance * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
                decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

                position = quantity;
                entryPrice = (decimal)currentPrice;

                // Учет комиссии на вход
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
                if (position > 0) // Закрываем длинную позицию
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

                // Расчет размера позиции с учетом ограничений (исправлено)
                // Расчет максимально допустимого размера позиции
                decimal maxPosition = balance * config.MaxPositionSizePercent / (decimal)currentPrice;

                // Проверка возможности открытия позиции
                if (maxPosition < config.MinOrderSize)
                {
                    logger.LogDebug($"Пропуск сделки: недостаточно средств для минимального ордера. Нужно: {config.MinOrderSize}, доступно: {maxPosition}");
                    continue;
                }

                decimal riskBasedQty = (balance * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
                decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

                position = -quantity;
                entryPrice = (decimal)currentPrice;

                // Учет комиссии на вход
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

        // Закрытие последней позиции
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

        // Расчет метрик
        double profitRatio = (double)(balance / config.InitialBalance);
        double sharpeRatio = CalculateSharpeRatio(equityCurve);
        double winRate = tradesCount > 0 ? (double)profitableTrades / tradesCount : 0;
        double maxDrawdownPercent = (double)maxDrawdown;

        return new BacktestResult(profitRatio, sharpeRatio, winRate, maxDrawdownPercent);
    }

    // Результаты бэктеста
    private record BacktestResult(
        double ProfitRatio,
        double SharpeRatio,
        double WinRate,
        double MaxDrawdown);

    // Запуск универсального бэктеста
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

            if (useMachineLearning)
            {
                logger.LogInformation("Подготовка модели машинного обучения...");
                var mlData = PrepareMLData(allKlines);
                if (mlData.Count == 0)
                {
                    logger.LogError("Не удалось подготовить данные для ML");
                    return;
                }
                TrainModel(mlData);
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
    mlContext.Model.CreatePredictionEngine<MarketData, MarketPrediction>(mlModel) : null;

            // Получаем данные старшего таймфрейма
            var higherTimeframeKlines = AggregateKlinesToHigherTimeframe(allKlines, config.HigherTimeframe);

            logger.LogInformation("Начало обработки данных...");
            for (int i = requiredBars; i < allKlines.Count; i++)
            {
                var currentKline = allKlines[i];
                var previousKlines = allKlines.Take(i).ToList();
                var closePrices = previousKlines.Select(k => (double)k.ClosePrice).ToArray();
                var currentPrice = (double)currentKline.ClosePrice;

                // Расчет ATR
                var atr = CalculateATR(previousKlines.Skip(i - 20).Take(20).ToList(), 20);
                var safeAtr = atr > 0 ? atr : (decimal)currentPrice * config.MinAtrPercent;

                // Расчет индикаторов
                var fastMa = CalculateSma(closePrices, parameters.FastMAPeriod);
                var slowMa = CalculateSma(closePrices, parameters.SlowMAPeriod);
                var rsi = CalculateRsi(closePrices, parameters.RSIPeriod);
                var (macdLine, signalLine, _) = CalculateMacd(closePrices, parameters.FastMAPeriod, parameters.SlowMAPeriod, 9);
                var (upperBand, middleBand, lowerBand) = CalculateBollingerBands(closePrices, parameters.BbPeriod, parameters.BbStdDev);

                // Расчет индикаторов для старшего таймфрейма
                var higherKlinesForTrend = higherTimeframeKlines
                    .Where(k => k.OpenTime <= currentKline.OpenTime)
                    .TakeLast(parameters.SlowMAPeriod / 2)
                    .ToList();

                if (higherKlinesForTrend.Count < 5) continue;

                var higherCloses = higherKlinesForTrend.Select(k => (double)k.ClosePrice).ToArray();
                var higherFastMa = CalculateSma(higherCloses, parameters.FastMAPeriod / 2);
                var higherSlowMa = CalculateSma(higherCloses, parameters.SlowMAPeriod / 2);

                // Фильтр силы тренда (добавлен)
                bool isStrongTrend = Math.Abs(higherFastMa - higherSlowMa) > (double)safeAtr * 0.5;

                // Определение тренда
                bool isHigherTrendBullish = higherFastMa > higherSlowMa;
                bool isHigherTrendBearish = higherFastMa < higherSlowMa;

                // Условия для входа
                bool isPrimaryBullish = fastMa > slowMa &&
                                      currentPrice > fastMa &&
                                      rsi < parameters.OverboughtLevel &&
                                      currentPrice > lowerBand &&
                                      macdLine > signalLine;

                bool isPrimaryBearish = fastMa < slowMa &&
                                      currentPrice < fastMa &&
                                      rsi > parameters.OversoldLevel &&
                                      currentPrice < upperBand &&
                                      macdLine < signalLine;

                bool mlConfirmation = true;
                if (useMachineLearning && predictionEngine != null && (isPrimaryBullish || isPrimaryBearish))
                {
                    var mlInput = new MarketData
                    {
                        Open = (float)currentKline.OpenPrice,
                        High = (float)currentKline.HighPrice,
                        Low = (float)currentKline.LowPrice,
                        Close = (float)currentKline.ClosePrice,
                        Volume = (float)currentKline.Volume,
                        RSI = (float)rsi,
                        MACD = (float)macdLine,
                        MACDSignal = (float)signalLine,
                        SMA5 = (float)CalculateSma(closePrices, 5),
                        SMA20 = (float)CalculateSma(closePrices, 20),
                        BBUpper = (float)upperBand,
                        BBLower = (float)lowerBand,
                        ATR = (float)safeAtr,
                        VolumeChange = (float)(currentKline.Volume / previousKlines.Average(k => k.Volume) - 1),
                        HigherTrend = (float)(higherFastMa - higherSlowMa),
                        MarketSentiment = (float)((currentPrice - middleBand) / (middleBand * 0.01))
                    };

                    var prediction = predictionEngine.Predict(mlInput);
                    mlConfirmation = prediction.ConfirmedPrediction;

                    if (isPrimaryBullish || isPrimaryBearish)
                    {
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
                }

                // Комбинированные условия с учетом тренда, ML и силы тренда
                bool isBullish = (isHigherTrendBullish || !isHigherTrendBearish) &&
                                isPrimaryBullish &&
                                (!useMachineLearning || mlConfirmation) &&
                                isStrongTrend;

                bool isBearish = (isHigherTrendBearish || !isHigherTrendBullish) &&
                                isPrimaryBearish &&
                                (!useMachineLearning || mlConfirmation) &&
                                isStrongTrend;

                // Закрытие позиции
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
                    else if (position > 0) // Длинная позиция
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
                        // Трейлинг-стоп
                        else if ((decimal)currentPrice >= entryPrice + safeAtr * 2 &&
                                (decimal)currentPrice <= entryPrice + safeAtr * 2 - safeAtr * 0.5m)
                        {
                            exitPrice = (decimal)currentPrice;
                            exitReason = "Trailing Stop";
                            shouldClose = true;
                        }
                    }
                    else // Короткая позиция
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
                        // Трейлинг-стоп
                        else if ((decimal)currentPrice <= entryPrice - safeAtr * 2 &&
                                (decimal)currentPrice >= entryPrice - safeAtr * 2 + safeAtr * 0.5m)
                        {
                            exitPrice = (decimal)currentPrice;
                            exitReason = "Trailing Stop";
                            shouldClose = true;
                        }
                    }

                    if (shouldClose)
                    {
                        decimal pnl = position > 0
                            ? position * (exitPrice - entryPrice)
                            : position * (entryPrice - exitPrice);

                        decimal commission = Math.Abs(position) * exitPrice * config.CommissionPercent / 100m;
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
                            exitPrice,
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

                // Открытие новой позиции
                if (isBullish && position <= 0)
                {
                    signalsGenerated++;

                    if (position < 0) // Закрываем короткую позицию
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

                    // Расчет размера позиции с учетом ограничений (исправлено)
                    // Расчет максимально допустимого размера позиции
                    decimal maxPosition = balance * config.MaxPositionSizePercent / (decimal)currentPrice;

                    // Проверка возможности открытия позиции
                    if (maxPosition < config.MinOrderSize)
                    {
                        logger.LogDebug($"Пропуск сделки: недостаточно средств для минимального ордера. Нужно: {config.MinOrderSize}, доступно: {maxPosition}");
                        continue;
                    }

                    decimal riskBasedQty = (balance * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
                    decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

                    position = quantity;
                    entryPrice = (decimal)currentPrice;

                    // Учет комиссии на вход
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

                    if (position > 0) // Закрываем длинную позицию
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

                    // Расчет размера позиции с учетом ограничений (исправлено)
                    // Расчет максимально допустимого размера позиции
                    decimal maxPosition = balance * config.MaxPositionSizePercent / (decimal)currentPrice;

                    // Проверка возможности открытия позиции
                    if (maxPosition < config.MinOrderSize)
                    {
                        logger.LogDebug($"Пропуск сделки: недостаточно средств для минимального ордера. Нужно: {config.MinOrderSize}, доступно: {maxPosition}");
                        continue;
                    }

                    decimal riskBasedQty = (balance * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
                    decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

                    position = -quantity;
                    entryPrice = (decimal)currentPrice;

                    // Учет комиссии на вход
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

            // Закрытие последней позиции
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

            // Расчет результатов
            decimal profit = balance - config.InitialBalance;
            decimal profitPercentage = (balance / config.InitialBalance - 1) * 100;
            int totalTrades = tradeHistory.Count(t => t.IsClosed);
            decimal winRate = totalTrades > 0 ?
                tradeHistory.Count(t => t.IsClosed && t.PnL > 0) * 100m / totalTrades : 0;

            // Исправленный расчет коэффициента Шарпа
            decimal sharpeRatio = (decimal)CalculateSharpeRatio(equityCurve, config.BacktestInterval == KlineInterval.OneHour);
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

            // Отправка результатов в Telegram
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

            // Сохранение истории сделок
            SaveTradeHistory(tradeHistory, useMachineLearning ? "BacktestWithML" : "Backtest");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка в RunBacktestUniversal");
            await telegramBot.SendMessage(config.TelegramChatId,
                $"❌ Ошибка при выполнении бэктеста: {ex.Message}");
        }
    }

    // Режим реальной торговли
    private static async Task RunLiveTrading(BinanceRestClient binanceClient, TelegramBotClient telegramBot)
    {
        logger.LogInformation("Запуск режима реальной торговли...");

        // Состояние трейлинг-стопа
        var trailingStopState = new TrailingStopState
        {
            ActivationPrice = 0,
            StopLevel = 0,
            IsActive = false
        };

        try
        {
            // Проверка необходимости переобучения модели
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

    // Состояние трейлинг-стопа
    private class TrailingStopState
    {
        public decimal ActivationPrice { get; set; }
        public decimal StopLevel { get; set; }
        public bool IsActive { get; set; }
    }

    // Проверка рынка и выполнение сделок
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

        // Проверка дневного лимита убытков
        if (dailyPnL <= -config.InitialBalance * config.MaxDailyLossPercent)
        {
            logger.LogWarning("Достигнут дневной лимит убытков. Торговля приостановлена до следующего дня.");
            return;
        }

        // Получение данных с разных таймфреймов
        var primaryKlines = await GetKlinesForTimeframe(binanceClient, config.PrimaryTimeframe);
        var higherKlines = await GetKlinesForTimeframe(binanceClient, config.HigherTimeframe);
        var lowerKlines = await GetKlinesForTimeframe(binanceClient, config.LowerTimeframe);

        if (primaryKlines == null || higherKlines == null || lowerKlines == null)
        {
            logger.LogError("Не удалось получить данные с одного из таймфреймов");
            return;
        }

        // Проверка фильтров
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

        // Расчет индикаторов для основного таймфрейма
        var primaryCloses = primaryKlines.Select(k => (double)k.ClosePrice).ToArray();
        var primaryFastMa = CalculateSma(primaryCloses, config.FastMAPeriod);
        var primarySlowMa = CalculateSma(primaryCloses, config.SlowMAPeriod);
        var primaryRsi = CalculateRsi(primaryCloses, config.RSIPeriod);
        var (primaryMacdLine, primarySignalLine, _) = CalculateMacd(primaryCloses, config.FastMAPeriod, config.SlowMAPeriod, 9);
        var (primaryUpperBb, primaryMiddleBb, primaryLowerBb) = CalculateBollingerBands(primaryCloses, config.BbPeriod, config.BbStdDev);

        // Расчет индикаторов для старшего таймфрейма
        var higherCloses = higherKlines.Select(k => (double)k.ClosePrice).ToArray();
        var higherFastMa = CalculateSma(higherCloses, config.FastMAPeriod);
        var higherSlowMa = CalculateSma(higherCloses, config.SlowMAPeriod);

        // Расчет индикаторов для младшего таймфрейма
        var lowerCloses = lowerKlines.Select(k => (double)k.ClosePrice).ToArray();
        var lowerFastMa = CalculateSma(lowerCloses, config.FastEmaPeriod / 2);
        var lowerSlowMa = CalculateSma(lowerCloses, config.SlowEmaPeriod / 2);
        var lowerRsi = CalculateRsi(lowerCloses, config.RSIPeriod / 2);
        var (lowerMacdLine, lowerSignalLine, _) = CalculateMacd(lowerCloses, config.FastEmaPeriod / 2, config.SlowEmaPeriod / 2, config.SignalPeriod / 2);
        var (lowerUpperBb, lowerMiddleBb, lowerLowerBb) = CalculateBollingerBands(lowerCloses, config.BbPeriod / 2, config.BbStdDev);

        // Расчет ATR
        var atrPeriod = (int)Math.Ceiling(24 / (decimal)config.PrimaryTimeframe.ToTimeSpan().TotalHours);
        var atr = CalculateATR(primaryKlines.TakeLast(atrPeriod).ToList(), atrPeriod);
        var safeAtr = atr > 0 ? atr : primaryKlines.Last().ClosePrice * config.MinAtrPercent;

        // Фильтр силы тренда (добавлен)
        bool isStrongTrend = Math.Abs(higherFastMa - higherSlowMa) > (double)safeAtr * 0.5;

        // Получение текущей цены с учетом проскальзывания
        var ticker = await binanceClient.SpotApi.ExchangeData.GetPriceAsync(config.Symbol);
        if (!ticker.Success)
        {
            logger.LogError("Ошибка получения цены: {Error}", ticker.Error);
            return;
        }
        var currentPrice = ticker.Data.Price;

        // Логирование текущего состояния
        logger.LogInformation(
            "{Time} | Цена: {Price} | MA{fastPeriod}: {FastMA} | MA{slowPeriod}: {SlowMA} | RSI: {RSI} | MACD: {MACD}/{Signal} | BB: {LowerBB}/{MiddleBB}/{UpperBB} | ATR: {ATR}",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            currentPrice.ToString("F2"),
            config.FastMAPeriod,
            primaryFastMa.ToString("F2"),
            config.SlowMAPeriod,
            primarySlowMa.ToString("F2"),
            primaryRsi.ToString("F2"),
            primaryMacdLine.ToString("F2"),
            primarySignalLine.ToString("F2"),
            primaryLowerBb.ToString("F2"),
            primaryMiddleBb.ToString("F2"),
            primaryUpperBb.ToString("F2"),
            safeAtr.ToString("F2"));

        // Определение тренда
        bool isHigherTrendBullish = higherFastMa > higherSlowMa;
        bool isHigherTrendBearish = higherFastMa < higherSlowMa;

        // Условия для входа
        bool isPrimaryBullish = primaryFastMa > primarySlowMa &&
                              currentPrice > (decimal)primaryFastMa &&
                              primaryRsi < config.OverboughtLevel &&
                              currentPrice > (decimal)primaryLowerBb &&
                              primaryMacdLine > primarySignalLine;

        bool isPrimaryBearish = primaryFastMa < primarySlowMa &&
                              currentPrice < (decimal)primaryFastMa &&
                              primaryRsi > config.OversoldLevel &&
                              currentPrice < (decimal)primaryUpperBb &&
                              primaryMacdLine < primarySignalLine;

        // Проверка ML подтверждения
        bool mlConfirmation = true;
        if (config.UseMachineLearning && (isPrimaryBullish || isPrimaryBearish))
        {
            var recentKlines = await GetKlinesForTimeframe(binanceClient, config.PrimaryTimeframe);
            if (recentKlines != null && recentKlines.Count >= config.MlLookbackPeriod)
            {
                mlConfirmation = await GetMLPrediction(recentKlines);
                logger.LogInformation("ML подтверждение: {0}", mlConfirmation ? "BUY" : "SELL");
            }
        }

        // Комбинированные условия с учетом тренда, ML и силы тренда
        bool isBullish = (isHigherTrendBullish || !isHigherTrendBearish) &&
                        isPrimaryBullish &&
                        (!config.UseMachineLearning || mlConfirmation) &&
                        isStrongTrend;

        bool isBearish = (isHigherTrendBearish || !isHigherTrendBullish) &&
                        isPrimaryBearish &&
                        (!config.UseMachineLearning || mlConfirmation) &&
                        isStrongTrend;

        // Проверка открытых позиций
        var positions = await GetOpenPositions(binanceClient);
        var currentPosition = positions.FirstOrDefault();

        // Управление трейлинг-стопом
        if (currentPosition != null)
        {
            if (currentPosition.Side == PositionSide.Long)
            {
                // Активация трейлинг-стопа
                if (!trailingStopState.IsActive && currentPrice >= currentPosition.EntryPrice + safeAtr * 2)
                {
                    trailingStopState.ActivationPrice = currentPrice;
                    trailingStopState.StopLevel = trailingStopState.ActivationPrice - safeAtr;
                    trailingStopState.IsActive = true;
                    logger.LogInformation($"Активирован трейлинг-стоп для длинной позиции. Уровень: {trailingStopState.StopLevel:F2}");
                }

                // Проверка трейлинг-стопа
                if (trailingStopState.IsActive)
                {
                    if (currentPrice > trailingStopState.ActivationPrice)
                    {
                        trailingStopState.ActivationPrice = currentPrice;
                        trailingStopState.StopLevel = trailingStopState.ActivationPrice - safeAtr;
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
            else // Короткая позиция
            {
                // Активация трейлинг-стопа
                if (!trailingStopState.IsActive && currentPrice <= currentPosition.EntryPrice - safeAtr * 2)
                {
                    trailingStopState.ActivationPrice = currentPrice;
                    trailingStopState.StopLevel = trailingStopState.ActivationPrice + safeAtr;
                    trailingStopState.IsActive = true;
                    logger.LogInformation($"Активирован трейлинг-стоп для короткой позиции. Уровень: {trailingStopState.StopLevel:F2}");
                }

                // Проверка трейлинг-стопа
                if (trailingStopState.IsActive)
                {
                    if (currentPrice < trailingStopState.ActivationPrice)
                    {
                        trailingStopState.ActivationPrice = currentPrice;
                        trailingStopState.StopLevel = trailingStopState.ActivationPrice + safeAtr;
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

            // Проверка времени удержания позиции
            var lastTrade = tradeHistory.LastOrDefault();
            if (lastTrade != null && (DateTime.Now - lastTrade.Timestamp).TotalHours >= 24)
            {
                logger.LogInformation("Закрытие позиции по истечении времени");
                await ClosePosition(binanceClient, telegramBot, currentPosition, currentPrice, "Time Exit");
                return;
            }
        }

        // Выполнение сделки, если нет открытых позиций
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

    // Закрытие позиции
    private static async Task ClosePosition(
        BinanceRestClient binanceClient,
        TelegramBotClient telegramBot,
        BinancePosition position,
        decimal currentPrice,
        string reason)
    {
        var orderSide = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;

        // Учет проскальзывания
        decimal executionPrice = orderSide == OrderSide.Sell ?
            currentPrice * 0.999m : // Продаем по цене ниже
            currentPrice * 1.001m;  // Покупаем по цене выше

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

            decimal commission = position.PositionAmount * executionPrice * config.CommissionPercent / 100m;
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
            await telegramBot.SendMessage(config.TelegramChatId, $"❌ Ошибка закрытия позиции: {order.Error}");
        }
    }

    // Выполнение сделки
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

        // Проверка баланса
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

        // Проверка открытых ордеров
        var openOrders = await binanceClient.SpotApi.Trading.GetOpenOrdersAsync(config.Symbol);
        if (openOrders.Success && openOrders.Data.Any())
        {
            logger.LogInformation("Есть открытые ордера, пропускаем новую сделку");
            return;
        }

        // Проверка открытых позиций
        var positions = await GetOpenPositions(binanceClient);
        if (positions.Any())
        {
            logger.LogInformation("Есть открытые позиции, пропускаем новую сделку");
            return;
        }

        // Расчет объема позиции с учетом ограничений (исправлено)
        decimal safeAtr = atr > 0 ? atr : currentPrice * config.MinAtrPercent;
        decimal maxPosition = usdtBalance.Value * config.MaxPositionSizePercent / currentPrice;
        decimal riskBasedQty = (usdtBalance.Value * config.RiskPerTrade) / (safeAtr * config.AtrMultiplierSL);
        decimal quantity = Math.Clamp(riskBasedQty, config.MinOrderSize, maxPosition);

        if (quantity <= config.MinOrderSize)
        {
            logger.LogWarning($"Рассчитанный объем {quantity} меньше минимального {config.MinOrderSize}");
            return;
        }

        // Учет проскальзывания
        decimal executionPrice = side == OrderSide.Buy ?
            currentPrice * 1.001m : // Покупаем по цене выше
            currentPrice * 0.999m;  // Продаем по цене ниже

        // Размещение ордера
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

            // Расчет комиссии
            decimal commission = quantity * executionPrice * config.CommissionPercent / 100m;
            dailyPnL -= commission;

            var message = $"{(side == OrderSide.Buy ? "🟢 КУПЛЕНО" : "🔴 ПРОДАНО")} {quantity:0.000000} {config.Symbol} по {executionPrice:0.00}\n" +
                          $"ATR: {safeAtr:0.00}, SL: {stopLossPrice:0.00}, TP: {takeProfitPrice:0.00}\n" +
                          $"Комиссия: {commission:0.00}";

            logger.LogInformation(message);
            await telegramBot.SendMessage(config.TelegramChatId, message);

            // Сохранение информации о сделке
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

            // Добавление позиции в список открытых
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

    // Получение открытых позиций
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

            // Поиск цены входа в позицию в истории сделок
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

    // Расчет простого скользящего среднего
    private static double CalculateSma(double[] closes, int period)
    {
        if (closes.Length < period)
        {
            logger.LogDebug($"Недостаточно данных для SMA{period}. Нужно: {period}, есть: {closes.Length}");
            return closes.Length > 0 ? closes.Average() : 0;
        }
        return closes.TakeLast(period).Average();
    }

    // Расчет RSI
    private static double CalculateRsi(double[] closes, int period)
    {
        if (closes.Length <= period)
        {
            logger.LogDebug($"Недостаточно данных для RSI{period}. Нужно: {period + 1}, есть: {closes.Length}");
            return 50;
        }

        var deltas = new double[closes.Length - 1];
        for (int i = 1; i < closes.Length; i++)
            deltas[i - 1] = closes[i] - closes[i - 1];

        // Инициализация первых значений
        double avgGain = deltas.Take(period).Where(d => d > 0).DefaultIfEmpty(0).Average();
        double avgLoss = Math.Abs(deltas.Take(period).Where(d => d < 0).DefaultIfEmpty(0).Average());

        // Сглаживание
        for (int i = period; i < deltas.Length; i++)
        {
            double delta = deltas[i];
            if (delta > 0)
            {
                avgGain = (avgGain * (period - 1) + delta) / period;
                avgLoss = (avgLoss * (period - 1)) / period;
            }
            else
            {
                avgGain = (avgGain * (period - 1)) / period;
                avgLoss = (avgLoss * (period - 1) + Math.Abs(delta)) / period;
            }
        }

        if (avgLoss == 0) return 100;
        double rs = avgGain / avgLoss;
        return 100 - (100 / (1 + rs));
    }

    // Расчет MACD
    private static (double macdLine, double signalLine, double histogram) CalculateMacd(double[] closes, int fastPeriod, int slowPeriod, int signalPeriod)
    {
        if (closes.Length < slowPeriod)
        {
            double lastValue = closes.Length > 0 ? closes.Last() : 0;
            return (lastValue, lastValue, 0);
        }

        // Расчет EMA
        double[] fastEmaValues = new double[closes.Length];
        double[] slowEmaValues = new double[closes.Length];

        double fastK = 2.0 / (fastPeriod + 1);
        double slowK = 2.0 / (slowPeriod + 1);

        fastEmaValues[0] = closes[0];
        slowEmaValues[0] = closes[0];

        for (int i = 1; i < closes.Length; i++)
        {
            fastEmaValues[i] = closes[i] * fastK + fastEmaValues[i - 1] * (1 - fastK);
            slowEmaValues[i] = closes[i] * slowK + slowEmaValues[i - 1] * (1 - slowK);
        }

        double macdLine = fastEmaValues.Last() - slowEmaValues.Last();

        if (closes.Length < slowPeriod + signalPeriod)
        {
            return (macdLine, macdLine, 0);
        }

        // Расчет сигнальной линии
        double[] macdValues = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
        {
            macdValues[i] = fastEmaValues[i] - slowEmaValues[i];
        }

        double[] signalLineValues = new double[macdValues.Length];
        double signalK = 2.0 / (signalPeriod + 1);
        signalLineValues[0] = macdValues[0];

        for (int i = 1; i < macdValues.Length; i++)
        {
            signalLineValues[i] = macdValues[i] * signalK + signalLineValues[i - 1] * (1 - signalK);
        }

        double signalLine = signalLineValues.Last();
        double histogram = macdLine - signalLine;

        return (macdLine, signalLine, histogram);
    }

    // Расчет EMA
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

    // Расчет полос Боллинджера
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

    // Расчет ATR
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

    // Получение предсказания от ML модели
    private static async Task<bool> GetMLPrediction(List<IBinanceKline> recentKlines)
    {
        try
        {
            if (!config.UseMachineLearning || recentKlines.Count < config.MlLookbackPeriod)
            {
                logger.LogDebug("ML отключен или недостаточно данных для предсказания");
                return true;
            }

            int aggregationFactor = GetAggregationFactor(config.PrimaryTimeframe, config.HigherTimeframe);
            var higherTimeframeKlines = AggregateKlinesToHigherTimeframe(recentKlines, config.HigherTimeframe);

            var closes = recentKlines.Select(k => (double)k.ClosePrice).ToArray();
            var volumes = recentKlines.Select(k => (double)k.Volume).ToArray();
            var rsi = CalculateRsi(closes, config.RSIPeriod);
            var (macdLine, signalLine, _) = CalculateMacd(closes, config.FastMAPeriod, config.SlowMAPeriod, 9);
            var sma5 = CalculateSma(closes, 5);
            var sma20 = CalculateSma(closes, 20);
            var (upperBB, middleBB, lowerBB) = CalculateBollingerBands(closes, config.BbPeriod, config.BbStdDev);
            var atr = (float)CalculateATR(recentKlines.TakeLast(14).ToList(), 14);
            var volumeChange = (float)(volumes.Last() / volumes.Take(volumes.Length - 1).Average() - 1);

            var higherCloses = higherTimeframeKlines
                .Where(k => k.OpenTime <= recentKlines.Last().OpenTime)
                .TakeLast(config.MlLookbackPeriod / aggregationFactor)
                .Select(k => (double)k.ClosePrice)
                .ToArray();

            var higherFastMa = CalculateSma(higherCloses, config.FastMAPeriod);
            var higherSlowMa = CalculateSma(higherCloses, config.SlowMAPeriod);
            float higherTrend = (float)(higherFastMa - higherSlowMa);

            float marketSentiment = (float)((closes.Last() - middleBB) / (middleBB * 0.01));

            var predictionData = new MarketData
            {
                Open = (float)recentKlines.Last().OpenPrice,
                High = (float)recentKlines.Last().HighPrice,
                Low = (float)recentKlines.Last().LowPrice,
                Close = (float)recentKlines.Last().ClosePrice,
                Volume = (float)recentKlines.Last().Volume,
                RSI = (float)rsi,
                MACD = (float)macdLine,
                MACDSignal = (float)signalLine,
                SMA5 = (float)sma5,
                SMA20 = (float)sma20,
                BBUpper = (float)upperBB,
                BBLower = (float)lowerBB,
                ATR = atr,
                VolumeChange = volumeChange,
                HigherTrend = higherTrend,
                MarketSentiment = marketSentiment,
                Target = false
            };

            var predictionEngine = mlContext.Model.CreatePredictionEngine<MarketData, MarketPrediction>(mlModel);
            var prediction = predictionEngine.Predict(predictionData);

            logger.LogDebug("ML Prediction: {Prediction} (Score: {Score:P2}, Confidence: {ConfidenceThreshold:P2})",
                prediction.PredictedLabel ? "BUY" : "SELL",
                prediction.Score,
                config.MlConfidenceThreshold);

            return prediction.ConfirmedPrediction;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при получении предсказания ML");
            return true;
        }
    }

    // Получение всех исторических данных
    private static async Task<List<IBinanceKline>> GetAllHistoricalData(BinanceRestClient binanceClient)
    {
        var allKlines = new List<IBinanceKline>();
        var currentStartTime = config.BacktestStartDate;

        try
        {
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

                await Task.Delay(250); // Ограничение запросов
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при получении исторических данных");
            return null;
        }

        logger.LogInformation("Получено {Count} свечей", allKlines.Count);
        return allKlines.Count > 0 ? allKlines : null;
    }

    // Проверка фильтра объема для бэктеста
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

        // Проверка объема в USDT (исправлено)
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

    // Проверка фильтра волатильности для бэктеста
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

    // Проверка фильтра объема для реальной торговли
    private static bool CheckLiveVolumeFilter(List<IBinanceKline> klines)
    {
        if (klines.Count < 2)
        {
            logger.LogDebug("Недостаточно данных для проверки объема");
            return false;
        }

        var currentKline = klines.Last();
        var prevKline = klines[^2];

        // Проверка объема в USDT (исправлено)
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

    // Проверка фильтра волатильности для реальной торговли
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

    // Получение данных по таймфрейму
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

    // Расчет коэффициента Шарпа (исправленный)
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

            // Безрисковая ставка (можно настроить)
            double riskFreeRate = 0.05 / 100; // 0.05% 

            // Корректировка для разных таймфреймов
            double annualizationFactor = isHourly ? Math.Sqrt(24 * 365) : Math.Sqrt(365);
            return (avgReturn - riskFreeRate) / stdDev * annualizationFactor;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка расчета коэффициента Шарпа");
            return 0;
        }
    }

    // Расчет максимальной просадки
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

    // Мутация параметров
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

    // Мутация значения
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

    // Сохранение истории сделок
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

    // Класс для хранения информации о позиции
    public class BinancePosition
    {
        public string Symbol { get; set; }
        public decimal PositionAmount { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal MarkPrice { get; set; }
        public decimal UnrealizedPnl { get; set; }
        public PositionSide Side { get; set; }
    }

    // Направление позиции
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
            KlineInterval.SixHour => TimeSpan.FromHours(6),
            KlineInterval.EightHour => TimeSpan.FromHours(8),
            KlineInterval.TwelveHour => TimeSpan.FromHours(12),
            KlineInterval.OneDay => TimeSpan.FromDays(1),
            KlineInterval.ThreeDay => TimeSpan.FromDays(3),
            KlineInterval.OneWeek => TimeSpan.FromDays(7),
            KlineInterval.OneMonth => TimeSpan.FromDays(30),
            _ => throw new ArgumentException($"Unsupported interval: {interval}")
        };
    }
}