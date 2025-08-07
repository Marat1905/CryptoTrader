//using Binance.Net.Clients;
//using Binance.Net.Enums;
//using Binance.Net.Interfaces;
//using CryptoExchange.Net.Authentication;
//using Microsoft.Extensions.Logging;
//using Microsoft.ML;
//using Microsoft.ML.Data;
//using Microsoft.ML.Trainers;
//using Microsoft.ML.Transforms;
//using System.Text;
//using Telegram.Bot;

//public class Program
//{
//    // Конфигурация бота
//    public class BotConfig
//    {
//        public string ApiKey { get; set; } = "YOUR_BINANCE_API_KEY";
//        public string ApiSecret { get; set; } = "YOUR_BINANCE_API_SECRET";
//        public string TelegramToken { get; set; } = "6299377057:AAHaNlY93hdrdQVanTPgmMibgQt41UDidRA";
//        public string TelegramChatId { get; set; } = "1314937104";
//        public string Symbol { get; set; } = "BTCUSDT";

//        // Управление рисками
//        public decimal RiskPerTrade { get; set; } = 0.02m;
//        public decimal StopLossPercent { get; set; } = 0.05m;
//        public decimal TakeProfitPercent { get; set; } = 0.10m;
//        public decimal MaxDailyLossPercent { get; set; } = 0.10m;

//        // Диапазоны параметров для оптимизации
//        public int[] FastMAPeriodRange { get; set; } = new[] { 5, 50 };
//        public int[] SlowMAPeriodRange { get; set; } = new[] { 15, 100 };
//        public int[] RSIPeriodRange { get; set; } = new[] { 10, 30 };
//        public double[] OverboughtLevelRange { get; set; } = new[] { 60.0, 80.0 };
//        public double[] OversoldLevelRange { get; set; } = new[] { 20.0, 40.0 };

//        // Фильтры
//        public decimal MinVolumeUSDT { get; set; } = 1000000m;
//        public decimal VolumeChangeThreshold { get; set; } = 0.5m;
//        public decimal VolatilityThreshold { get; set; } = 0.02m;
//        public int VolatilityPeriod { get; set; } = 14;

//        // Таймфреймы
//        public KlineInterval PrimaryTimeframe { get; set; } = KlineInterval.OneHour;
//        public KlineInterval HigherTimeframe { get; set; } = KlineInterval.FourHour;
//        public KlineInterval LowerTimeframe { get; set; } = KlineInterval.FifteenMinutes;

//        // Параметры по умолчанию
//        public int FastMAPeriod { get; set; } = 12;
//        public int SlowMAPeriod { get; set; } = 26;
//        public int RSIPeriod { get; set; } = 14;
//        public double OverboughtLevel { get; set; } = 70.0;
//        public double OversoldLevel { get; set; } = 30.0;
//        public int FastEmaPeriod { get; set; } = 12;
//        public int SlowEmaPeriod { get; set; } = 26;
//        public int SignalPeriod { get; set; } = 9;
//        public int BbPeriod { get; set; } = 20;
//        public double BbStdDev { get; set; } = 2.0;

//        // Настройки ML
//        public int MlLookbackPeriod { get; set; } = 100;
//        public int MlPredictionHorizon { get; set; } = 5;
//        public double MlConfidenceThreshold { get; set; } = 0.7;
//        public bool UseMachineLearning { get; set; } = true;
//        public int MlTrainingIntervalHours { get; set; } = 24;
//        public int MlWindowSize { get; set; } = 200;

//        // Настройки оптимизации
//        public int OptimizationGenerations { get; set; } = 20;
//        public int OptimizationPopulationSize { get; set; } = 50;
//        public int CheckIntervalMinutes { get; set; } = 5;

//        // Режимы работы
//        public bool BacktestMode { get; set; } = true;
//        public bool OptimizeMode { get; set; } = true;
//        public DateTime BacktestStartDate { get; set; } = new DateTime(2023, 1, 1);
//        public DateTime BacktestEndDate { get; set; } = DateTime.Now;
//        public KlineInterval BacktestInterval { get; set; } = KlineInterval.OneHour;
//        public decimal InitialBalance { get; set; } = 1000m;
//    }

//    // Параметры торговой стратегии
//    public record TradingParams(
//        int FastMAPeriod,
//        int SlowMAPeriod,
//        int RSIPeriod,
//        double OverboughtLevel,
//        double OversoldLevel,
//        int FastEmaPeriod,
//        int SlowEmaPeriod,
//        int SignalPeriod,
//        int BbPeriod,
//        double BbStdDev)
//    {
//        public override string ToString() =>
//            $"FastMA={FastMAPeriod}, SlowMA={SlowMAPeriod}, RSI={RSIPeriod}, " +
//            $"OB={OverboughtLevel:F1}, OS={OversoldLevel:F1}, " +
//            $"MACD(F={FastEmaPeriod},S={SlowEmaPeriod},Sig={SignalPeriod}), " +
//            $"BB(P={BbPeriod},SD={BbStdDev:F1})";
//    }

//    // Запись о сделке
//    public record TradeRecord(
//        DateTime Timestamp,
//        string Type,
//        decimal Quantity,
//        decimal EntryPrice,
//        decimal ExitPrice,
//        decimal StopLossPrice,
//        decimal TakeProfitPrice,
//        decimal PnL,
//        string Notes = "")
//    {
//        public bool IsClosed => ExitPrice != 0;
//    }

//    // Структура данных для ML
//    public class MarketData
//    {
//        [LoadColumn(0)] public float Open { get; set; }
//        [LoadColumn(1)] public float High { get; set; }
//        [LoadColumn(2)] public float Low { get; set; }
//        [LoadColumn(3)] public float Close { get; set; }
//        [LoadColumn(4)] public float Volume { get; set; }
//        [LoadColumn(5)] public float RSI { get; set; }
//        [LoadColumn(6)] public float MACD { get; set; }
//        [LoadColumn(7)] public float MACDSignal { get; set; }
//        [LoadColumn(8)] public float SMA5 { get; set; }
//        [LoadColumn(9)] public float SMA20 { get; set; }
//        [LoadColumn(10)] public float BBUpper { get; set; }
//        [LoadColumn(11)] public float BBLower { get; set; }
//        [ColumnName("Label")] // Добавлено это
//        public bool Target { get; set; } // Это будет использоваться как Label
//    }

//    // Результат предсказания ML
//    public class MarketPrediction
//    {
//        [ColumnName("PredictedLabel")]
//        public bool PredictedLabel { get; set; } // Исправлено с float на bool

//        [ColumnName("Score")]
//        public float Score { get; set; } // Вероятность положительного класса

//        // Удобное свойство для проверки уверенности модели
//        public bool ConfirmedPrediction => PredictedLabel && Score >= 0.7f;
//    }

//    private static BotConfig config = new BotConfig();
//    private static ILogger logger;
//    private static decimal dailyPnL = 0;
//    private static DateTime lastTradeDate = DateTime.MinValue;
//    private static MLContext mlContext = new MLContext();
//    private static ITransformer mlModel;
//    private static DateTime lastModelTrainingTime = DateTime.MinValue;
//    private static List<TradeRecord> tradeHistory = new List<TradeRecord>();

//    public static async Task Main(string[] args)
//    {
//        // Настройка логгера
//        using var loggerFactory = LoggerFactory.Create(builder =>
//        {
//            builder.AddConsole();
//            builder.SetMinimumLevel(LogLevel.Debug);
//        });

//        logger = loggerFactory.CreateLogger("CryptoBot");

//        try
//        {
//            // Инициализация клиентов
//            var binanceClient = new BinanceRestClient(options =>
//            {
//                options.ApiCredentials = new ApiCredentials(config.ApiKey, config.ApiSecret);
//            });

//            var telegramBot = new TelegramBotClient(config.TelegramToken);

//            // Режим бэктеста
//            if (config.BacktestMode)
//            {
//                var defaultParams = new TradingParams(
//                    config.FastMAPeriod,
//                    config.SlowMAPeriod,
//                    config.RSIPeriod,
//                    config.OverboughtLevel,
//                    config.OversoldLevel,
//                    config.FastEmaPeriod,
//                    config.SlowEmaPeriod,
//                    config.SignalPeriod,
//                    config.BbPeriod,
//                    config.BbStdDev);

//                // Бэктест с параметрами по умолчанию
//                await RunBacktestUniversal(
//                    binanceClient,
//                    telegramBot,
//                    "с параметрами по умолчанию",
//                    defaultParams);

//                // Бэктест с интеграцией ML
//                if (config.UseMachineLearning)
//                {
//                    await RunBacktestUniversal(
//                        binanceClient,
//                        telegramBot,
//                        "с интеграцией ML",
//                        defaultParams,
//                        true);
//                }
//            }

//            // Режим оптимизации
//            if (config.OptimizeMode)
//            {
//                await OptimizeParameters(binanceClient, telegramBot);
//            }

//            // Бэктест с оптимизированными параметрами
//            if (config.BacktestMode)
//            {
//                var optimizedParams = new TradingParams(
//                    config.FastMAPeriod,
//                    config.SlowMAPeriod,
//                    config.RSIPeriod,
//                    config.OverboughtLevel,
//                    config.OversoldLevel,
//                    config.FastEmaPeriod,
//                    config.SlowEmaPeriod,
//                    config.SignalPeriod,
//                    config.BbPeriod,
//                    config.BbStdDev);

//                await RunBacktestUniversal(
//                    binanceClient,
//                    telegramBot,
//                    "с оптимизированными параметрами",
//                    optimizedParams);
//            }
//            else
//            {
//                // Режим реальной торговли
//                logger.LogInformation("Бот запущен в реальном режиме. Мониторинг рынка...");
//                await TrainInitialModel(binanceClient);
//                await RunLiveTrading(binanceClient, telegramBot);
//            }
//        }
//        catch (Exception ex)
//        {
//            logger.LogCritical(ex, "Критическая ошибка в главном методе");
//        }
//    }

//    // Обучение начальной модели ML
//    private static async Task TrainInitialModel(BinanceRestClient binanceClient)
//    {
//        try
//        {
//            logger.LogInformation("Начало обучения начальной модели машинного обучения...");

//            var historicalData = await GetAllHistoricalData(binanceClient);
//            if (historicalData == null || historicalData.Count == 0)
//            {
//                logger.LogError("Не удалось получить исторические данные для обучения модели");
//                return;
//            }

//            var mlData = PrepareMLData(historicalData);
//            if (mlData.Count == 0)
//            {
//                logger.LogError("Не удалось подготовить данные для обучения модели");
//                return;
//            }

//            TrainModel(mlData);
//            lastModelTrainingTime = DateTime.Now;
//            logger.LogInformation("Начальная модель машинного обучения успешно обучена");
//        }
//        catch (Exception ex)
//        {
//            logger.LogError(ex, "Ошибка при обучении начальной модели");
//        }
//    }

//    // Подготовка данных для ML
//    private static List<MarketData> PrepareMLData(List<IBinanceKline> klines)
//    {
//        var mlData = new List<MarketData>();
//        if (klines.Count < config.MlLookbackPeriod + config.MlPredictionHorizon)
//        {
//            logger.LogWarning($"Недостаточно данных для ML. Нужно: {config.MlLookbackPeriod + config.MlPredictionHorizon}, есть: {klines.Count}");
//            return mlData;
//        }

//        for (int i = config.MlLookbackPeriod; i < klines.Count - config.MlPredictionHorizon; i++)
//        {
//            var currentWindow = klines.Skip(i - config.MlLookbackPeriod).Take(config.MlLookbackPeriod).ToList();
//            var futurePrices = klines.Skip(i).Take(config.MlPredictionHorizon).Select(k => (double)k.ClosePrice).ToArray();

//            if (currentWindow.Count < config.MlLookbackPeriod || futurePrices.Length < config.MlPredictionHorizon)
//                continue;

//            var closes = currentWindow.Select(k => (double)k.ClosePrice).ToArray();
//            var rsi = CalculateRsi(closes, config.RSIPeriod);
//            var (macdLine, signalLine, _) = CalculateMacd(closes, config.FastEmaPeriod, config.SlowEmaPeriod, config.SignalPeriod);
//            var sma5 = CalculateSma(closes, 5);
//            var sma20 = CalculateSma(closes, 20);
//            var (upperBB, _, lowerBB) = CalculateBollingerBands(closes, config.BbPeriod, config.BbStdDev);

//            double currentPrice = closes.Last();
//            double futureMaxPrice = futurePrices.Max();
//            double futureMinPrice = futurePrices.Min();
//            bool willRise = (futureMaxPrice - currentPrice) > (currentPrice - futureMinPrice);

//            mlData.Add(new MarketData
//            {
//                Open = (float)currentWindow.Last().OpenPrice,
//                High = (float)currentWindow.Last().HighPrice,
//                Low = (float)currentWindow.Last().LowPrice,
//                Close = (float)currentWindow.Last().ClosePrice,
//                Volume = (float)currentWindow.Last().Volume,
//                RSI = (float)rsi,
//                MACD = (float)macdLine,
//                MACDSignal = (float)signalLine,
//                SMA5 = (float)sma5,
//                SMA20 = (float)sma20,
//                BBUpper = (float)upperBB,
//                BBLower = (float)lowerBB,
//                Target = willRise
//            });
//        }

//        logger.LogInformation($"Подготовлено {mlData.Count} записей для обучения ML");
//        return mlData;
//    }

//    // Обучение модели ML
//    private static void TrainModel(List<MarketData> trainingData)
//    {
//        try
//        {
//            if (trainingData.Count < 100)
//            {
//                logger.LogWarning($"Слишком мало данных для обучения: {trainingData.Count} записей");
//                return;
//            }

//            IDataView dataView = mlContext.Data.LoadFromEnumerable(trainingData);

//            // Конвейер обработки данных
//            var dataProcessPipeline = mlContext.Transforms
//                .Concatenate("Features",
//                    nameof(MarketData.Open),
//                    nameof(MarketData.High),
//                    nameof(MarketData.Low),
//                    nameof(MarketData.Close),
//                    nameof(MarketData.Volume),
//                    nameof(MarketData.RSI),
//                    nameof(MarketData.MACD),
//                    nameof(MarketData.MACDSignal),
//                    nameof(MarketData.SMA5),
//                    nameof(MarketData.SMA20),
//                    nameof(MarketData.BBUpper),
//                    nameof(MarketData.BBLower))
//                .Append(mlContext.Transforms.NormalizeMinMax("Features"));

//            // Изменено на использование "Label" вместо nameof(MarketData.Target)
//            var trainer = mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
//                labelColumnName: "Label",
//                featureColumnName: "Features");

//            var trainingPipeline = dataProcessPipeline.Append(trainer);

//            // Обучение модели
//            mlModel = trainingPipeline.Fit(dataView);

//            // Кросс-валидация
//            var cvResults = mlContext.BinaryClassification.CrossValidate(
//                dataView,
//                trainingPipeline,
//                numberOfFolds: 5);

//            var avgAccuracy = cvResults.Average(r => r.Metrics.Accuracy);
//            logger.LogInformation($"Модель обучена. Средняя точность по кросс-валидации: {avgAccuracy:P2}");
//        }
//        catch (Exception ex)
//        {
//            logger.LogError(ex, "Ошибка при обучении модели");
//            throw;
//        }
//    }

//    // Получение предсказания от ML модели
//    private static async Task<bool> GetMLPrediction(List<IBinanceKline> recentKlines)
//    {
//        try
//        {
//            if (!config.UseMachineLearning || recentKlines.Count < config.MlLookbackPeriod)
//            {
//                logger.LogDebug("ML отключен или недостаточно данных для предсказания");
//                return true; // Разрешить сделку, если ML не используется
//            }

//            // Подготовка данных для предсказания
//            var predictionData = new MarketData
//            {
//                Open = (float)recentKlines.Last().OpenPrice,
//                High = (float)recentKlines.Last().HighPrice,
//                Low = (float)recentKlines.Last().LowPrice,
//                Close = (float)recentKlines.Last().ClosePrice,
//                Volume = (float)recentKlines.Last().Volume,
//                RSI = (float)CalculateRsi(recentKlines.Select(k => (double)k.ClosePrice).ToArray(), config.RSIPeriod),
//                MACD = (float)CalculateMacd(recentKlines.Select(k => (double)k.ClosePrice).ToArray(),
//                    config.FastEmaPeriod, config.SlowEmaPeriod, config.SignalPeriod).macdLine,
//                MACDSignal = (float)CalculateMacd(recentKlines.Select(k => (double)k.ClosePrice).ToArray(),
//                    config.FastEmaPeriod, config.SlowEmaPeriod, config.SignalPeriod).signalLine,
//                SMA5 = (float)CalculateSma(recentKlines.Select(k => (double)k.ClosePrice).ToArray(), 5),
//                SMA20 = (float)CalculateSma(recentKlines.Select(k => (double)k.ClosePrice).ToArray(), 20),
//                BBUpper = (float)CalculateBollingerBands(recentKlines.Select(k => (double)k.ClosePrice).ToArray(),
//                    config.BbPeriod, config.BbStdDev).upperBand,
//                BBLower = (float)CalculateBollingerBands(recentKlines.Select(k => (double)k.ClosePrice).ToArray(),
//                    config.BbPeriod, config.BbStdDev).lowerBand,
//                Target = false // Не используется для предсказания
//            };

//            // Создание движка для предсказания
//            var predictionEngine = mlContext.Model.CreatePredictionEngine<MarketData, MarketPrediction>(mlModel);
//            var prediction = predictionEngine.Predict(predictionData);

//            logger.LogDebug("ML Prediction: {Prediction} (Score: {Score:P2}, Confidence: {ConfidenceThreshold:P2})",
//                prediction.PredictedLabel ? "BUY" : "SELL",
//                prediction.Score,
//                config.MlConfidenceThreshold);

//            // Возвращаем true только если модель уверена в росте цены
//            return prediction.ConfirmedPrediction;
//        }
//        catch (Exception ex)
//        {
//            logger.LogError(ex, "Ошибка при получении предсказания ML");
//            return true; // Разрешить сделку в случае ошибки
//        }
//    }

//    // Оптимизация параметров
//    private static async Task OptimizeParameters(BinanceRestClient binanceClient, TelegramBotClient telegramBot)
//    {
//        logger.LogInformation("Запуск оптимизации параметров...");

//        var allKlines = await GetAllHistoricalData(binanceClient);
//        if (allKlines == null || !allKlines.Any())
//        {
//            logger.LogError("Не удалось получить данные для оптимизации");
//            return;
//        }

//        logger.LogInformation("=== БАКТЕСТ ПЕРЕД ОПТИМИЗАЦИЕЙ ===");
//        var defaultParams = new TradingParams(
//            config.FastMAPeriod,
//            config.SlowMAPeriod,
//            config.RSIPeriod,
//            config.OverboughtLevel,
//            config.OversoldLevel,
//            config.FastEmaPeriod,
//            config.SlowEmaPeriod,
//            config.SignalPeriod,
//            config.BbPeriod,
//            config.BbStdDev);

//        var defaultScore = EvaluateParameters(allKlines, defaultParams);
//        logger.LogInformation($"Результат до оптимизации: {defaultScore:F2} с параметрами: {defaultParams}");

//        var random = new Random();
//        var bestScore = double.MinValue;
//        var bestParams = defaultParams;

//        for (int generation = 0; generation < config.OptimizationGenerations; generation++)
//        {
//            logger.LogInformation($"Поколение {generation + 1}/{config.OptimizationGenerations}");

//            var population = new List<TradingParams>();

//            // Первое поколение - случайные параметры
//            if (generation == 0)
//            {
//                for (int i = 0; i < config.OptimizationPopulationSize; i++)
//                {
//                    population.Add(new TradingParams(
//                        random.Next(config.FastMAPeriodRange[0], config.FastMAPeriodRange[1]),
//                        random.Next(config.SlowMAPeriodRange[0], config.SlowMAPeriodRange[1]),
//                        random.Next(config.RSIPeriodRange[0], config.RSIPeriodRange[1]),
//                        config.OverboughtLevelRange[0] + random.NextDouble() * (config.OverboughtLevelRange[1] - config.OverboughtLevelRange[0]),
//                        config.OversoldLevelRange[0] + random.NextDouble() * (config.OversoldLevelRange[1] - config.OversoldLevelRange[0]),
//                        random.Next(8, 20),
//                        random.Next(20, 35),
//                        random.Next(5, 15),
//                        random.Next(10, 30),
//                        1.5 + random.NextDouble() * 1.5));
//                }
//            }
//            else
//            {
//                // Последующие поколения - мутация лучших параметров
//                for (int i = 0; i < config.OptimizationPopulationSize; i++)
//                {
//                    population.Add(MutateParams(bestParams, random));
//                }
//            }

//            // Оценка каждого набора параметров
//            foreach (var paramSet in population)
//            {
//                var result = EvaluateParameters(allKlines, paramSet);

//                if (result > bestScore)
//                {
//                    bestScore = result;
//                    bestParams = paramSet;
//                    logger.LogInformation($"Новый лучший результат: {bestScore:F2} с параметрами: {bestParams}");
//                }
//            }
//        }

//        logger.LogInformation("=== БАКТЕСТ ПОСЛЕ ОПТИМИЗАЦИИ ===");
//        var optimizedScore = EvaluateParameters(allKlines, bestParams);
//        logger.LogInformation($"Результат после оптимизации: {optimizedScore:F2} с параметрами: {bestParams}");

//        logger.LogInformation("\n=== РЕЗУЛЬТАТЫ ОПТИМИЗАЦИИ ===");
//        logger.LogInformation($"Результат до оптимизации: {defaultScore:F2}");
//        logger.LogInformation($"Лучший результат: {bestScore:F2}");
//        logger.LogInformation($"Лучшие параметры: {bestParams}");

//        await telegramBot.SendMessage(
//            chatId: config.TelegramChatId,
//            text: $"🎯 Результаты оптимизации {config.Symbol}\n" +
//                  $"До оптимизации: {defaultScore:F2}\n" +
//                  $"После оптимизации: {bestScore:F2}\n" +
//                  $"Параметры: {bestParams}");

//        // Обновляем конфигурацию лучшими параметрами
//        config.FastMAPeriod = bestParams.FastMAPeriod;
//        config.SlowMAPeriod = bestParams.SlowMAPeriod;
//        config.RSIPeriod = bestParams.RSIPeriod;
//        config.OverboughtLevel = bestParams.OverboughtLevel;
//        config.OversoldLevel = bestParams.OversoldLevel;
//        config.FastEmaPeriod = bestParams.FastEmaPeriod;
//        config.SlowEmaPeriod = bestParams.SlowEmaPeriod;
//        config.SignalPeriod = bestParams.SignalPeriod;
//        config.BbPeriod = bestParams.BbPeriod;
//        config.BbStdDev = bestParams.BbStdDev;
//    }

//    // Мутация параметров
//    private static TradingParams MutateParams(TradingParams bestParams, Random random)
//    {
//        return new TradingParams(
//            MutateValue(bestParams.FastMAPeriod, config.FastMAPeriodRange[0], config.FastMAPeriodRange[1], random),
//            MutateValue(bestParams.SlowMAPeriod, config.SlowMAPeriodRange[0], config.SlowMAPeriodRange[1], random),
//            MutateValue(bestParams.RSIPeriod, config.RSIPeriodRange[0], config.RSIPeriodRange[1], random),
//            MutateValue(bestParams.OverboughtLevel, config.OverboughtLevelRange[0], config.OverboughtLevelRange[1], random),
//            MutateValue(bestParams.OversoldLevel, config.OversoldLevelRange[0], config.OversoldLevelRange[1], random),
//            MutateValue(bestParams.FastEmaPeriod, 8, 20, random),
//            MutateValue(bestParams.SlowEmaPeriod, 20, 35, random),
//            MutateValue(bestParams.SignalPeriod, 5, 15, random),
//            MutateValue(bestParams.BbPeriod, 10, 30, random),
//            Math.Max(1.0, Math.Min(3.0, bestParams.BbStdDev + (random.NextDouble() - 0.5) * 0.5)));
//    }

//    // Метод для мутации значений параметров
//    private static T MutateValue<T>(T value, T min, T max, Random random) where T : struct
//    {
//        if (random.NextDouble() >= 0.3) // 70% вероятность оставить значение без изменений
//            return value;

//        if (typeof(T) == typeof(int))
//        {
//            int val = (int)(object)value;
//            int minVal = (int)(object)min;
//            int maxVal = (int)(object)max;
//            int change = random.Next(-3, 4); // Изменение от -3 до +3
//            int newValue = val + change;
//            return (T)(object)Math.Min(maxVal, Math.Max(minVal, newValue));
//        }
//        else if (typeof(T) == typeof(double))
//        {
//            double val = (double)(object)value;
//            double minVal = (double)(object)min;
//            double maxVal = (double)(object)max;
//            double change = (random.NextDouble() - 0.5) * (maxVal - minVal) * 0.2;
//            double newValue = val + change;
//            return (T)(object)Math.Min(maxVal, Math.Max(minVal, newValue));
//        }

//        throw new NotSupportedException($"Тип {typeof(T)} не поддерживается");
//    }

//    // Оценка параметров стратегии
//    private static double EvaluateParameters(List<IBinanceKline> allKlines, TradingParams parameters)
//    {
//        decimal balance = config.InitialBalance;
//        decimal position = 0;
//        decimal entryPrice = 0;
//        decimal stopLossPrice = 0;
//        decimal takeProfitPrice = 0;
//        var equityCurve = new List<decimal>();
//        int tradesCount = 0;
//        int profitableTrades = 0;

//        int requiredBars = new[] {
//            parameters.SlowMAPeriod,
//            parameters.RSIPeriod,
//            parameters.BbPeriod,
//            config.VolatilityPeriod
//        }.Max() + 1;

//        if (allKlines.Count < requiredBars)
//        {
//            logger.LogWarning($"Недостаточно данных для оценки. Требуется: {requiredBars}, получено: {allKlines.Count}");
//            return 0;
//        }

//        for (int i = requiredBars; i < allKlines.Count; i++)
//        {
//            var currentKline = allKlines[i];
//            var previousKlines = allKlines.Take(i).ToList();

//            if (!CheckVolumeFilter(previousKlines, i)) continue;
//            if (!CheckVolatilityFilter(previousKlines, i)) continue;

//            var closePrices = previousKlines.Select(k => (double)k.ClosePrice).ToArray();
//            var currentPrice = (double)currentKline.ClosePrice;

//            var fastMa = CalculateSma(closePrices, parameters.FastMAPeriod);
//            var slowMa = CalculateSma(closePrices, parameters.SlowMAPeriod);
//            var rsi = CalculateRsi(closePrices, parameters.RSIPeriod);
//            var (macdLine, signalLine, _) = CalculateMacd(closePrices, parameters.FastEmaPeriod, parameters.SlowEmaPeriod, parameters.SignalPeriod);
//            var (upperBand, middleBand, lowerBand) = CalculateBollingerBands(closePrices, parameters.BbPeriod, parameters.BbStdDev);

//            // Закрытие позиции по стоп-лоссу или тейк-профиту
//            if (position != 0)
//            {
//                bool shouldClose = false;
//                decimal exitPrice = 0;

//                if (position > 0) // Длинная позиция
//                {
//                    if ((decimal)currentPrice <= stopLossPrice)
//                    {
//                        exitPrice = stopLossPrice;
//                        shouldClose = true;
//                    }
//                    else if ((decimal)currentPrice >= takeProfitPrice)
//                    {
//                        exitPrice = takeProfitPrice;
//                        shouldClose = true;
//                    }
//                }
//                else // Короткая позиция
//                {
//                    if ((decimal)currentPrice >= stopLossPrice)
//                    {
//                        exitPrice = stopLossPrice;
//                        shouldClose = true;
//                    }
//                    else if ((decimal)currentPrice <= takeProfitPrice)
//                    {
//                        exitPrice = takeProfitPrice;
//                        shouldClose = true;
//                    }
//                }

//                if (shouldClose)
//                {
//                    decimal pnl = position > 0
//                        ? position * (exitPrice - entryPrice)
//                        : position * (entryPrice - exitPrice);

//                    balance += pnl;
//                    tradesCount++;
//                    if (pnl > 0) profitableTrades++;

//                    position = 0;
//                    equityCurve.Add(balance);
//                    continue;
//                }
//            }

//            // Условия для входа в сделку
//            bool isBullish = fastMa > slowMa &&
//                            closePrices[^2] <= slowMa &&
//                            rsi < parameters.OverboughtLevel &&
//                            macdLine > signalLine &&
//                            currentPrice < lowerBand;

//            bool isBearish = fastMa < slowMa &&
//                            closePrices[^2] >= slowMa &&
//                            rsi > parameters.OversoldLevel &&
//                            macdLine < signalLine &&
//                            currentPrice > upperBand;

//            // Вход в сделку
//            if (isBullish && position <= 0)
//            {
//                if (position < 0) // Закрываем короткую позицию
//                {
//                    decimal pnl = position * ((decimal)currentPrice - entryPrice);
//                    balance += pnl;
//                    tradesCount++;
//                    if (pnl > 0) profitableTrades++;
//                }

//                decimal quantity = (balance * config.RiskPerTrade) / (decimal)currentPrice;
//                position = quantity;
//                entryPrice = (decimal)currentPrice;
//                stopLossPrice = entryPrice * (1 - config.StopLossPercent);
//                takeProfitPrice = entryPrice * (1 + config.TakeProfitPercent);
//            }
//            else if (isBearish && position >= 0)
//            {
//                if (position > 0) // Закрываем длинную позицию
//                {
//                    decimal pnl = position * ((decimal)currentPrice - entryPrice);
//                    balance += pnl;
//                    tradesCount++;
//                    if (pnl > 0) profitableTrades++;
//                }

//                decimal quantity = (balance * config.RiskPerTrade) / (decimal)currentPrice;
//                position = -quantity;
//                entryPrice = (decimal)currentPrice;
//                stopLossPrice = entryPrice * (1 + config.StopLossPercent);
//                takeProfitPrice = entryPrice * (1 - config.TakeProfitPercent);
//            }

//            equityCurve.Add(balance + position * ((decimal)currentPrice - entryPrice));
//        }

//        // Закрытие последней позиции
//        if (position != 0)
//        {
//            var lastPrice = (double)allKlines.Last().ClosePrice;
//            var pnl = position * ((decimal)lastPrice - entryPrice);
//            balance += pnl;
//            tradesCount++;
//            if (pnl > 0) profitableTrades++;
//        }

//        // Расчет метрик
//        double profitRatio = (double)(balance / config.InitialBalance);
//        double sharpeRatio = CalculateSharpeRatio(equityCurve);
//        double winRate = tradesCount > 0 ? (double)profitableTrades / tradesCount : 0;
//        double maxDrawdown = (double)CalculateMaxDrawdown(equityCurve);

//        // Комбинированная оценка (фитнес-функция)
//        return profitRatio * 0.5 + sharpeRatio * 0.3 + winRate * 0.2 - maxDrawdown * 0.1;
//    }

//    // Проверка фильтра объема
//    private static bool CheckVolumeFilter(List<IBinanceKline> klines, int currentIndex)
//    {
//        if (klines == null || klines.Count == 0 || currentIndex < 0 || currentIndex >= klines.Count)
//            return false;

//        if (currentIndex < 2)
//            return true;

//        var currentKline = klines[currentIndex - 1];
//        var prevKline = klines[currentIndex - 2];

//        if (currentKline == null || prevKline == null)
//            return false;

//        if (currentKline.Volume * currentKline.ClosePrice < config.MinVolumeUSDT)
//        {
//            logger.LogDebug($"Фильтр объема не пройден: {currentKline.Volume * currentKline.ClosePrice} < {config.MinVolumeUSDT}");
//            return false;
//        }

//        if (prevKline.Volume == 0)
//            return true;

//        var volumeChange = Math.Abs((currentKline.Volume - prevKline.Volume) / prevKline.Volume);
//        bool passed = volumeChange >= config.VolumeChangeThreshold;

//        if (!passed)
//            logger.LogDebug($"Фильтр изменения объема не пройден: {volumeChange:P2} < {config.VolumeChangeThreshold:P2}");

//        return passed;
//    }

//    // Проверка фильтра волатильности
//    private static bool CheckVolatilityFilter(List<IBinanceKline> klines, int currentIndex)
//    {
//        if (currentIndex < config.VolatilityPeriod)
//        {
//            logger.LogDebug("Недостаточно данных для проверки волатильности");
//            return true;
//        }

//        var relevantKlines = klines.Skip(currentIndex - config.VolatilityPeriod).Take(config.VolatilityPeriod).ToList();
//        var atr = CalculateATR(relevantKlines, config.VolatilityPeriod);

//        var currentPrice = klines[currentIndex].ClosePrice;
//        var volatility = atr / currentPrice;

//        bool passed = volatility >= config.VolatilityThreshold;

//        if (!passed)
//            logger.LogDebug($"Фильтр волатильности не пройден: {volatility:P2} < {config.VolatilityThreshold:P2}");

//        return passed;
//    }

//    // Расчет ATR (Average True Range)
//    private static decimal CalculateATR(List<IBinanceKline> klines, int period)
//    {
//        var trueRanges = new List<double>();

//        for (int i = 1; i < klines.Count; i++)
//        {
//            var current = klines[i];
//            var previous = klines[i - 1];

//            double highLow = (double)(current.HighPrice - current.LowPrice);
//            double highClose = Math.Abs((double)(current.HighPrice - previous.ClosePrice));
//            double lowClose = Math.Abs((double)(current.LowPrice - previous.ClosePrice));

//            trueRanges.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
//        }

//        if (trueRanges.Count < period) return 0;
//        return (decimal)trueRanges.TakeLast(period).Average();
//    }

//    // Расчет коэффициента Шарпа
//    private static double CalculateSharpeRatio(List<decimal> equityCurve)
//    {
//        if (equityCurve.Count < 2) return 0;

//        var dailyReturns = new List<double>();
//        for (int i = 1; i < equityCurve.Count; i++)
//        {
//            double ret = (double)((equityCurve[i] - equityCurve[i - 1]) / equityCurve[i - 1]);
//            dailyReturns.Add(ret);
//        }

//        if (!dailyReturns.Any()) return 0;

//        double avgReturn = dailyReturns.Average();
//        double stdDev = Math.Sqrt(dailyReturns.Sum(r => Math.Pow(r - avgReturn, 2)) / dailyReturns.Count);

//        if (stdDev == 0) return 0;
//        return avgReturn / stdDev * Math.Sqrt(365);
//    }

//    // Получение всех исторических данных
//    private static async Task<List<IBinanceKline>> GetAllHistoricalData(BinanceRestClient binanceClient)
//    {
//        var allKlines = new List<IBinanceKline>();
//        var currentStartTime = config.BacktestStartDate;

//        try
//        {
//            while (currentStartTime < config.BacktestEndDate)
//            {
//                var klinesResult = await binanceClient.SpotApi.ExchangeData.GetKlinesAsync(
//                    config.Symbol,
//                    config.BacktestInterval,
//                    startTime: currentStartTime,
//                    endTime: config.BacktestEndDate,
//                    limit: 1000);

//                if (!klinesResult.Success)
//                {
//                    logger.LogError("Ошибка получения данных: {Error}", klinesResult.Error);
//                    return null;
//                }

//                if (!klinesResult.Data.Any())
//                    break;

//                allKlines.AddRange(klinesResult.Data);
//                currentStartTime = klinesResult.Data.Last().OpenTime.AddMinutes(1);

//                await Task.Delay(250); // Ограничение запросов
//            }
//        }
//        catch (Exception ex)
//        {
//            logger.LogError(ex, "Ошибка при получении исторических данных");
//            return null;
//        }

//        logger.LogInformation("Получено {Count} свечей", allKlines.Count);
//        return allKlines.Count > 0 ? allKlines : null;
//    }

//    // Универсальный метод для бэктеста
//    private static async Task RunBacktestUniversal(
//        BinanceRestClient binanceClient,
//        TelegramBotClient telegramBot,
//        string description,
//        TradingParams parameters = null,
//        bool useMachineLearning = false)
//    {
//        try
//        {
//            parameters ??= new TradingParams(
//                config.FastMAPeriod,
//                config.SlowMAPeriod,
//                config.RSIPeriod,
//                config.OverboughtLevel,
//                config.OversoldLevel,
//                config.FastEmaPeriod,
//                config.SlowEmaPeriod,
//                config.SignalPeriod,
//                config.BbPeriod,
//                config.BbStdDev);

//            logger.LogInformation("=== НАЧАЛО БЭКТЕСТА ===");
//            logger.LogInformation($"Описание: {description}");
//            logger.LogInformation($"Параметры: {parameters}");
//            if (useMachineLearning) logger.LogInformation("С ИСПОЛЬЗОВАНИЕМ МАШИННОГО ОБУЧЕНИЯ");
//            logger.LogInformation($"Период: {config.BacktestStartDate:yyyy-MM-dd} - {config.BacktestEndDate:yyyy-MM-dd}");
//            logger.LogInformation($"Таймфрейм: {config.BacktestInterval}");
//            logger.LogInformation($"Начальный баланс: {config.InitialBalance}");

//            logger.LogInformation("Загрузка исторических данных...");
//            var allKlines = await GetAllHistoricalData(binanceClient);

//            if (allKlines == null || allKlines.Count == 0)
//            {
//                logger.LogError("Не удалось получить исторические данные");
//                await telegramBot.SendMessage(config.TelegramChatId, "❌ Ошибка: не удалось получить исторические данные");
//                return;
//            }

//            if (useMachineLearning)
//            {
//                logger.LogInformation("Подготовка модели машинного обучения...");
//                var mlData = PrepareMLData(allKlines);
//                if (mlData.Count == 0)
//                {
//                    logger.LogError("Не удалось подготовить данные для ML");
//                    return;
//                }
//                TrainModel(mlData);
//            }

//            decimal balance = config.InitialBalance;
//            decimal position = 0;
//            decimal entryPrice = 0;
//            var tradeHistory = new List<TradeRecord>();
//            var equityCurve = new List<decimal> { balance };
//            int signalsGenerated = 0;
//            int tradesExecuted = 0;
//            int mlConfirmations = 0;
//            int mlRejections = 0;

//            int requiredBars = new[] {
//                parameters.SlowMAPeriod,
//                parameters.RSIPeriod,
//                parameters.BbPeriod,
//                config.VolatilityPeriod,
//                useMachineLearning ? config.MlLookbackPeriod : 0
//            }.Max() + 1;

//            if (allKlines.Count < requiredBars)
//            {
//                logger.LogError($"Недостаточно данных. Требуется: {requiredBars}, получено: {allKlines.Count}");
//                return;
//            }

//            var predictionEngine = useMachineLearning ?
//                mlContext.Model.CreatePredictionEngine<MarketData, MarketPrediction>(mlModel) : null;

//            logger.LogInformation("Начало обработки данных...");
//            for (int i = requiredBars; i < allKlines.Count; i++)
//            {
//                var currentKline = allKlines[i];
//                var previousKlines = allKlines.Take(i).ToList();
//                var closePrices = previousKlines.Select(k => (double)k.ClosePrice).ToArray();
//                var currentPrice = (double)currentKline.ClosePrice;

//                var fastMa = CalculateSma(closePrices, parameters.FastMAPeriod);
//                var slowMa = CalculateSma(closePrices, parameters.SlowMAPeriod);
//                var rsi = CalculateRsi(closePrices, parameters.RSIPeriod);
//                var (macdLine, signalLine, _) = CalculateMacd(closePrices, parameters.FastEmaPeriod, parameters.SlowEmaPeriod, parameters.SignalPeriod);
//                var (upperBand, middleBand, lowerBand) = CalculateBollingerBands(closePrices, parameters.BbPeriod, parameters.BbStdDev);

//                if (i % 100 == 0)
//                {
//                    logger.LogDebug($"Свеча {i}: Time={currentKline.OpenTime}, Price={currentPrice:F2}, " +
//                        $"MA{parameters.FastMAPeriod}={fastMa:F2}, MA{parameters.SlowMAPeriod}={slowMa:F2}, " +
//                        $"RSI={rsi:F2}, MACD={macdLine:F2}/{signalLine:F2}, " +
//                        $"BB={lowerBand:F2}/{middleBand:F2}/{upperBand:F2}");
//                }

//                bool isBullish = fastMa > slowMa &&
//                               closePrices[^2] <= slowMa &&
//                               rsi < parameters.OverboughtLevel &&
//                               macdLine > signalLine &&
//                               currentPrice < lowerBand;

//                bool isBearish = fastMa < slowMa &&
//                                closePrices[^2] >= slowMa &&
//                                rsi > parameters.OversoldLevel &&
//                                macdLine < signalLine &&
//                                currentPrice > upperBand;

//                bool mlConfirmation = true;
//                if (useMachineLearning && predictionEngine != null && (isBullish || isBearish) && i >= config.MlLookbackPeriod)
//                {
//                    var mlInput = new MarketData
//                    {
//                        Open = (float)currentKline.OpenPrice,
//                        High = (float)currentKline.HighPrice,
//                        Low = (float)currentKline.LowPrice,
//                        Close = (float)currentKline.ClosePrice,
//                        Volume = (float)currentKline.Volume,
//                        RSI = (float)rsi,
//                        MACD = (float)macdLine,
//                        MACDSignal = (float)signalLine,
//                        SMA5 = (float)CalculateSma(closePrices, 5),
//                        SMA20 = (float)CalculateSma(closePrices, 20),
//                        BBUpper = (float)upperBand,
//                        BBLower = (float)lowerBand
//                    };

//                    var prediction = predictionEngine.Predict(mlInput);
//                    mlConfirmation = prediction.ConfirmedPrediction;

//                    if (isBullish || isBearish)
//                    {
//                        if (mlConfirmation)
//                        {
//                            mlConfirmations++;
//                            logger.LogDebug($"ML подтвердил сигнал: {(isBullish ? "BUY" : "SELL")} (Score: {prediction.Score:P2})");
//                        }
//                        else
//                        {
//                            mlRejections++;
//                            logger.LogDebug($"ML отклонил сигнал: {(isBullish ? "BUY" : "SELL")} (Score: {prediction.Score:P2})");
//                        }
//                    }
//                }

//                // Закрытие позиции
//                if (position != 0)
//                {
//                    bool shouldClose = false;
//                    decimal exitPrice = 0;
//                    string exitReason = "";

//                    if (position > 0) // Длинная позиция
//                    {
//                        if ((decimal)currentPrice <= entryPrice * (1m - config.StopLossPercent))
//                        {
//                            exitPrice = entryPrice * (1m - config.StopLossPercent);
//                            exitReason = "SL";
//                            shouldClose = true;
//                        }
//                        else if ((decimal)currentPrice >= entryPrice * (1m + config.TakeProfitPercent))
//                        {
//                            exitPrice = entryPrice * (1m + config.TakeProfitPercent);
//                            exitReason = "TP";
//                            shouldClose = true;
//                        }
//                    }
//                    else // Короткая позиция
//                    {
//                        if ((decimal)currentPrice >= entryPrice * (1m + config.StopLossPercent))
//                        {
//                            exitPrice = entryPrice * (1m + config.StopLossPercent);
//                            exitReason = "SL";
//                            shouldClose = true;
//                        }
//                        else if ((decimal)currentPrice <= entryPrice * (1m - config.TakeProfitPercent))
//                        {
//                            exitPrice = entryPrice * (1m - config.TakeProfitPercent);
//                            exitReason = "TP";
//                            shouldClose = true;
//                        }
//                    }

//                    if (shouldClose)
//                    {
//                        decimal pnl = position > 0
//                            ? position * (exitPrice - entryPrice)
//                            : position * (entryPrice - exitPrice);

//                        balance += pnl;
//                        tradesExecuted++;

//                        tradeHistory.Add(new TradeRecord(
//                            currentKline.OpenTime,
//                            position > 0 ? $"SELL ({exitReason})" : $"BUY ({exitReason})",
//                            Math.Abs(position),
//                            entryPrice,
//                            exitPrice,
//                            position > 0 ? entryPrice * (1m - config.StopLossPercent) : entryPrice * (1m + config.StopLossPercent),
//                            position > 0 ? entryPrice * (1m + config.TakeProfitPercent) : entryPrice * (1m - config.TakeProfitPercent),
//                            pnl,
//                            $"Закрытие по {exitReason}"));

//                        position = 0;
//                        equityCurve.Add(balance);
//                        continue;
//                    }
//                }

//                // Открытие новой позиции
//                if (isBullish && position <= 0 && (!useMachineLearning || mlConfirmation))
//                {
//                    signalsGenerated++;

//                    if (position < 0) // Закрываем короткую позицию
//                    {
//                        decimal pnl = position * ((decimal)currentPrice - entryPrice);
//                        balance += pnl;
//                        tradesExecuted++;

//                        tradeHistory.Add(new TradeRecord(
//                            currentKline.OpenTime,
//                            "BUY (Close)",
//                            Math.Abs(position),
//                            entryPrice,
//                            (decimal)currentPrice,
//                            0, 0, pnl,
//                            "Закрытие короткой позиции перед открытием длинной"));
//                    }

//                    decimal quantity = (balance * config.RiskPerTrade) / (decimal)currentPrice;
//                    position = quantity;
//                    entryPrice = (decimal)currentPrice;

//                    tradeHistory.Add(new TradeRecord(
//                        currentKline.OpenTime,
//                        useMachineLearning ? "BUY (ML)" : "BUY",
//                        quantity,
//                        entryPrice,
//                        0,
//                        entryPrice * (1m - config.StopLossPercent),
//                        entryPrice * (1m + config.TakeProfitPercent),
//                        0,
//                        $"Открытие длинной позиции. Условия: MA Fast > Slow, RSI < OB, MACD > Signal, Price < BB Lower"));
//                }
//                else if (isBearish && position >= 0 && (!useMachineLearning || !mlConfirmation))
//                {
//                    signalsGenerated++;

//                    if (position > 0) // Закрываем длинную позицию
//                    {
//                        decimal pnl = position * ((decimal)currentPrice - entryPrice);
//                        balance += pnl;
//                        tradesExecuted++;

//                        tradeHistory.Add(new TradeRecord(
//                            currentKline.OpenTime,
//                            "SELL (Close)",
//                            position,
//                            entryPrice,
//                            (decimal)currentPrice,
//                            0, 0, pnl,
//                            "Закрытие длинной позиции перед открытием короткой"));
//                    }

//                    decimal quantity = (balance * config.RiskPerTrade) / (decimal)currentPrice;
//                    position = -quantity;
//                    entryPrice = (decimal)currentPrice;

//                    tradeHistory.Add(new TradeRecord(
//                        currentKline.OpenTime,
//                        useMachineLearning ? "SELL (ML)" : "SELL",
//                        quantity,
//                        entryPrice,
//                        0,
//                        entryPrice * (1m + config.StopLossPercent),
//                        entryPrice * (1m - config.TakeProfitPercent),
//                        0,
//                        $"Открытие короткой позиции. Условия: MA Fast < Slow, RSI > OS, MACD < Signal, Price > BB Upper"));
//                }

//                equityCurve.Add(balance + position * ((decimal)currentPrice - entryPrice));
//            }

//            // Закрытие последней позиции
//            if (position != 0)
//            {
//                var lastPrice = (double)allKlines.Last().ClosePrice;
//                decimal pnl = position * ((decimal)lastPrice - entryPrice);
//                balance += pnl;
//                tradesExecuted++;

//                tradeHistory.Add(new TradeRecord(
//                    allKlines.Last().OpenTime,
//                    position > 0 ? "SELL (Close)" : "BUY (Close)",
//                    Math.Abs(position),
//                    entryPrice,
//                    (decimal)lastPrice,
//                    0, 0, pnl,
//                    "Принудительное закрытие позиции в конце теста"));
//            }

//            // Расчет результатов
//            decimal profit = balance - config.InitialBalance;
//            decimal profitPercentage = (balance / config.InitialBalance - 1) * 100;
//            int totalTrades = tradeHistory.Count(t => t.IsClosed);
//            decimal winRate = totalTrades > 0 ?
//                tradeHistory.Count(t => t.IsClosed && t.PnL > 0) * 100m / totalTrades : 0;
//            decimal maxDrawdown = CalculateMaxDrawdown(equityCurve);

//            logger.LogInformation("\n=== РЕЗУЛЬТАТЫ БЭКТЕСТА ===");
//            logger.LogInformation($"Сигналов сгенерировано: {signalsGenerated}");
//            if (useMachineLearning)
//            {
//                logger.LogInformation($"Подтверждено ML: {mlConfirmations}");
//                logger.LogInformation($"Отклонено ML: {mlRejections}");
//            }
//            logger.LogInformation($"Сделок выполнено: {tradesExecuted}");
//            logger.LogInformation($"Конечный баланс: {balance:F2}");
//            logger.LogInformation($"Прибыль: {profit:F2} ({profitPercentage:F2}%)");
//            logger.LogInformation($"Процент прибыльных сделок: {winRate:F2}%");
//            logger.LogInformation($"Максимальная просадка: {maxDrawdown:F2}%");

//            // Отправка результатов в Telegram
//            var message = new StringBuilder();
//            message.AppendLine($"📊 Результаты бэктеста {description}: {config.Symbol}");
//            message.AppendLine($"Период: {config.BacktestStartDate:dd.MM.yyyy} - {config.BacktestEndDate:dd.MM.yyyy}");
//            message.AppendLine($"Таймфрейм: {config.BacktestInterval}");
//            message.AppendLine($"Баланс: {config.InitialBalance:F2} → {balance:F2}");
//            message.AppendLine($"Прибыль: {profit:F2} ({profitPercentage:F2}%)");
//            message.AppendLine($"Сделок: {totalTrades} | Прибыльных: {winRate:F2}%");
//            message.AppendLine($"Просадка: {maxDrawdown:F2}%");

//            if (useMachineLearning)
//            {
//                message.AppendLine($"ML подтвердил: {mlConfirmations} | Отклонил: {mlRejections}");
//            }

//            await telegramBot.SendMessage(config.TelegramChatId, message.ToString());

//            // Сохранение истории сделок
//            SaveTradeHistory(tradeHistory, useMachineLearning ? "BacktestWithML" : "Backtest");
//        }
//        catch (Exception ex)
//        {
//            logger.LogError(ex, "Ошибка в RunBacktestUniversal");
//            await telegramBot.SendMessage(config.TelegramChatId,
//                $"❌ Ошибка при выполнении бэктеста: {ex.Message}");
//        }
//    }

//    // Режим реальной торговли
//    private static async Task RunLiveTrading(BinanceRestClient binanceClient, TelegramBotClient telegramBot)
//    {
//        logger.LogInformation("Запуск режима реальной торговли...");

//        while (true)
//        {
//            try
//            {
//                // Проверяем, не нужно ли переобучить модель
//                if (config.UseMachineLearning &&
//                    (DateTime.Now - lastModelTrainingTime).TotalHours >= config.MlTrainingIntervalHours)
//                {
//                    logger.LogInformation("Переобучение модели ML...");
//                    await TrainInitialModel(binanceClient);
//                }

//                await CheckMarketAndTradeAsync(binanceClient, telegramBot);
//                await Task.Delay(TimeSpan.FromMinutes(config.CheckIntervalMinutes));
//            }
//            catch (Exception ex)
//            {
//                logger.LogError(ex, "Ошибка в основном цикле торговли");
//                await Task.Delay(TimeSpan.FromSeconds(30));
//            }
//        }
//    }

//    // Проверка рынка и выполнение сделок
//    private static async Task CheckMarketAndTradeAsync(BinanceRestClient binanceClient, TelegramBotClient telegramBot)
//    {
//        if (DateTime.Now.Date != lastTradeDate.Date)
//        {
//            dailyPnL = 0;
//            lastTradeDate = DateTime.Now.Date;
//            logger.LogInformation("Новый торговый день. Сброс дневного PnL.");
//        }

//        if (dailyPnL <= -config.InitialBalance * config.MaxDailyLossPercent)
//        {
//            logger.LogWarning("Достигнут дневной лимит убытков. Торговля приостановлена до следующего дня.");
//            return;
//        }

//        // Получение данных с разных таймфреймов
//        var primaryKlines = await GetKlinesForTimeframe(binanceClient, config.PrimaryTimeframe);
//        var higherKlines = await GetKlinesForTimeframe(binanceClient, config.HigherTimeframe);
//        var lowerKlines = await GetKlinesForTimeframe(binanceClient, config.LowerTimeframe);

//        if (primaryKlines == null || higherKlines == null || lowerKlines == null)
//        {
//            logger.LogError("Не удалось получить данные с одного из таймфреймов");
//            return;
//        }

//        // Проверка фильтров
//        if (!CheckLiveVolumeFilter(primaryKlines))
//        {
//            logger.LogDebug("Фильтр объема не пройден");
//            return;
//        }

//        if (!CheckLiveVolatilityFilter(primaryKlines))
//        {
//            logger.LogDebug("Фильтр волатильности не пройден");
//            return;
//        }

//        // Расчет индикаторов для основного таймфрейма
//        var primaryCloses = primaryKlines.Select(k => (double)k.ClosePrice).ToArray();
//        var primaryFastMa = CalculateSma(primaryCloses, config.FastMAPeriod);
//        var primarySlowMa = CalculateSma(primaryCloses, config.SlowMAPeriod);
//        var primaryRsi = CalculateRsi(primaryCloses, config.RSIPeriod);
//        var (primaryMacdLine, primarySignalLine, _) = CalculateMacd(primaryCloses, config.FastEmaPeriod, config.SlowEmaPeriod, config.SignalPeriod);
//        var (primaryUpperBb, primaryMiddleBb, primaryLowerBb) = CalculateBollingerBands(primaryCloses, config.BbPeriod, config.BbStdDev);

//        // Расчет индикаторов для старшего таймфрейма
//        var higherCloses = higherKlines.Select(k => (double)k.ClosePrice).ToArray();
//        var higherFastMa = CalculateSma(higherCloses, config.FastMAPeriod);
//        var higherSlowMa = CalculateSma(higherCloses, config.SlowMAPeriod);

//        // Расчет индикаторов для младшего таймфрейма
//        var lowerCloses = lowerKlines.Select(k => (double)k.ClosePrice).ToArray();
//        var lowerFastMa = CalculateSma(lowerCloses, config.FastMAPeriod / 2);
//        var lowerSlowMa = CalculateSma(lowerCloses, config.SlowMAPeriod / 2);
//        var lowerRsi = CalculateRsi(lowerCloses, config.RSIPeriod / 2);
//        var (lowerMacdLine, lowerSignalLine, _) = CalculateMacd(lowerCloses, config.FastEmaPeriod / 2, config.SlowEmaPeriod / 2, config.SignalPeriod / 2);
//        var (lowerUpperBb, lowerMiddleBb, lowerLowerBb) = CalculateBollingerBands(lowerCloses, config.BbPeriod / 2, config.BbStdDev);

//        // Получение текущей цены
//        var ticker = await binanceClient.SpotApi.ExchangeData.GetPriceAsync(config.Symbol);
//        if (!ticker.Success)
//        {
//            logger.LogError("Ошибка получения цены: {Error}", ticker.Error);
//            return;
//        }
//        var currentPrice = (double)ticker.Data.Price;

//        // Логирование текущего состояния
//        logger.LogInformation(
//            "{Time} | Цена: {Price} | MA{fastPeriod}: {FastMA} | MA{slowPeriod}: {SlowMA} | RSI: {RSI} | MACD: {MACD}/{Signal} | BB: {LowerBB}/{MiddleBB}/{UpperBB}",
//            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
//            currentPrice.ToString("F2"),
//            config.FastMAPeriod,
//            primaryFastMa.ToString("F2"),
//            config.SlowMAPeriod,
//            primarySlowMa.ToString("F2"),
//            primaryRsi.ToString("F2"),
//            primaryMacdLine.ToString("F2"),
//            primarySignalLine.ToString("F2"),
//            primaryLowerBb.ToString("F2"),
//            primaryMiddleBb.ToString("F2"),
//            primaryUpperBb.ToString("F2"));

//        // Определение тренда на старшем таймфрейме
//        bool isHigherTrendBullish = higherFastMa > higherSlowMa;
//        bool isHigherTrendBearish = higherFastMa < higherSlowMa;

//        // Условия для входа на основном таймфрейме
//        bool isPrimaryBullish = primaryFastMa > primarySlowMa &&
//                              primaryCloses[^2] <= primarySlowMa &&
//                              primaryRsi < config.OverboughtLevel &&
//                              primaryMacdLine > primarySignalLine &&
//                              currentPrice < primaryLowerBb;

//        bool isPrimaryBearish = primaryFastMa < primarySlowMa &&
//                               primaryCloses[^2] >= primarySlowMa &&
//                               primaryRsi > config.OversoldLevel &&
//                               primaryMacdLine < primarySignalLine &&
//                               currentPrice > primaryUpperBb;

//        // Условия для подтверждения на младшем таймфрейме
//        bool isLowerBullish = lowerFastMa > lowerSlowMa &&
//                             lowerCloses[^2] <= lowerSlowMa &&
//                             lowerRsi < config.OversoldLevel &&
//                             lowerMacdLine > lowerSignalLine &&
//                             currentPrice < lowerLowerBb;

//        bool isLowerBearish = lowerFastMa < lowerSlowMa &&
//                              lowerCloses[^2] >= lowerSlowMa &&
//                              lowerRsi > config.OverboughtLevel &&
//                              lowerMacdLine < lowerSignalLine &&
//                              currentPrice > lowerUpperBb;

//        // Комбинированные условия
//        bool isBullish = (isHigherTrendBullish || !isHigherTrendBearish) && isPrimaryBullish && isLowerBullish;
//        bool isBearish = (isHigherTrendBearish || !isHigherTrendBullish) && isPrimaryBearish && isLowerBearish;

//        // Проверка ML подтверждения
//        if (config.UseMachineLearning && (isBullish || isBearish))
//        {
//            var recentKlines = await GetKlinesForTimeframe(binanceClient, config.PrimaryTimeframe);
//            if (recentKlines != null && recentKlines.Count >= config.MlLookbackPeriod)
//            {
//                var mlConfirmation = await GetMLPrediction(recentKlines);
//                isBullish = isBullish && mlConfirmation;
//                isBearish = isBearish && !mlConfirmation;

//                logger.LogInformation("ML подтверждение: {0}", mlConfirmation ? "BUY" : "SELL");
//            }
//        }

//        // Выполнение сделки
//        if (isBullish)
//        {
//            await ExecuteTradeAsync(binanceClient, telegramBot, OrderSide.Buy, (decimal)currentPrice);
//        }
//        else if (isBearish)
//        {
//            await ExecuteTradeAsync(binanceClient, telegramBot, OrderSide.Sell, (decimal)currentPrice);
//        }
//    }

//    // Получение данных для таймфрейма
//    private static async Task<List<IBinanceKline>> GetKlinesForTimeframe(BinanceRestClient client, KlineInterval timeframe)
//    {
//        int requiredBars = Math.Max(Math.Max(config.SlowMAPeriod, config.RSIPeriod), config.BbPeriod) + 50;

//        var klinesResult = await client.SpotApi.ExchangeData.GetKlinesAsync(
//            config.Symbol,
//            timeframe,
//            limit: requiredBars);

//        if (!klinesResult.Success)
//        {
//            logger.LogError("Ошибка получения свечей для таймфрейма {0}: {1}", timeframe, klinesResult.Error);
//            return null;
//        }

//        return klinesResult.Data.ToList();
//    }

//    // Проверка фильтра объема в реальном времени
//    private static bool CheckLiveVolumeFilter(List<IBinanceKline> klines)
//    {
//        if (klines.Count < 2)
//        {
//            logger.LogDebug("Недостаточно данных для проверки объема");
//            return false;
//        }

//        var currentKline = klines.Last();
//        var prevKline = klines[^2];

//        if (currentKline.Volume * currentKline.ClosePrice < config.MinVolumeUSDT)
//        {
//            logger.LogDebug($"Фильтр объема не пройден: {currentKline.Volume * currentKline.ClosePrice} < {config.MinVolumeUSDT}");
//            return false;
//        }

//        if (prevKline.Volume == 0)
//            return true;

//        var volumeChange = Math.Abs((currentKline.Volume - prevKline.Volume) / prevKline.Volume);
//        bool passed = volumeChange >= config.VolumeChangeThreshold;

//        if (!passed)
//            logger.LogDebug($"Фильтр изменения объема не пройден: {volumeChange:P2} < {config.VolumeChangeThreshold:P2}");

//        return passed;
//    }

//    // Проверка фильтра волатильности в реальном времени
//    private static bool CheckLiveVolatilityFilter(List<IBinanceKline> klines)
//    {
//        if (klines.Count < config.VolatilityPeriod)
//        {
//            logger.LogDebug("Недостаточно данных для проверки волатильности");
//            return false;
//        }

//        var relevantKlines = klines.TakeLast(config.VolatilityPeriod).ToList();
//        var atr = CalculateATR(relevantKlines, config.VolatilityPeriod);

//        var currentPrice = klines.Last().ClosePrice;
//        var volatility = atr / currentPrice;

//        bool passed = volatility >= config.VolatilityThreshold;

//        if (!passed)
//            logger.LogDebug($"Фильтр волатильности не пройден: {volatility:P2} < {config.VolatilityThreshold:P2}");

//        return passed;
//    }

//    // Выполнение сделки
//    private static async Task ExecuteTradeAsync(BinanceRestClient binanceClient, TelegramBotClient telegramBot, OrderSide side, decimal currentPrice)
//    {
//        if (DateTime.Now.Date != lastTradeDate.Date)
//        {
//            dailyPnL = 0;
//            lastTradeDate = DateTime.Now.Date;
//        }

//        if (dailyPnL <= -config.InitialBalance * config.MaxDailyLossPercent)
//        {
//            logger.LogWarning("Достигнут дневной лимит убытков. Торговля приостановлена до следующего дня.");
//            return;
//        }

//        // Проверка баланса
//        var accountInfo = await binanceClient.SpotApi.Account.GetAccountInfoAsync();
//        if (!accountInfo.Success)
//        {
//            logger.LogError("Ошибка получения баланса: {Error}", accountInfo.Error);
//            return;
//        }

//        var usdtBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == "USDT")?.Available;
//        if (usdtBalance is null or <= 10)
//        {
//            logger.LogWarning("Недостаточно USDT для торговли");
//            return;
//        }

//        // Проверка открытых ордеров
//        var openOrders = await binanceClient.SpotApi.Trading.GetOpenOrdersAsync(config.Symbol);
//        if (openOrders.Success && openOrders.Data.Any())
//        {
//            logger.LogInformation("Есть открытые ордера, пропускаем новую сделку");
//            return;
//        }

//        // Проверка открытых позиций
//        var positions = await GetOpenPositions(binanceClient);
//        if (positions.Any())
//        {
//            logger.LogInformation("Есть открытые позиции, пропускаем новую сделку");
//            return;
//        }

//        // Расчет объема позиции
//        decimal quantity = (usdtBalance.Value * config.RiskPerTrade) / currentPrice;
//        quantity = Math.Round(quantity, 6); // Округление до допустимого количества знаков

//        // Размещение ордера
//        var order = await binanceClient.SpotApi.Trading.PlaceOrderAsync(
//            config.Symbol,
//            side,
//            SpotOrderType.Market,
//            quantity: quantity);

//        if (order.Success)
//        {
//            var message = $"{(side == OrderSide.Buy ? "🟢 КУПЛЕНО" : "🔴 ПРОДАНО")} {quantity:0.000000} {config.Symbol} по {currentPrice:0.00}";
//            logger.LogInformation(message);
//            await telegramBot.SendMessage(
//                chatId: config.TelegramChatId,
//                text: message);

//            // Расчет уровней стоп-лосса и тейк-профита
//            decimal stopLossPrice = side == OrderSide.Buy
//                ? currentPrice * (1 - config.StopLossPercent)
//                : currentPrice * (1 + config.StopLossPercent);

//            decimal takeProfitPrice = side == OrderSide.Buy
//                ? currentPrice * (1 + config.TakeProfitPercent)
//                : currentPrice * (1 - config.TakeProfitPercent);

//            logger.LogInformation("Стоп-лосс: {0}, Тейк-профит: {1}",
//                stopLossPrice.ToString("0.00"),
//                takeProfitPrice.ToString("0.00"));

//            // В реальной торговле здесь можно разместить лимитные ордера на закрытие
//            // или начать отслеживание цены для ручного закрытия
//        }
//        else
//        {
//            logger.LogError("Ошибка ордера: {Error}", order.Error);
//            await telegramBot.SendMessage(
//                chatId: config.TelegramChatId,
//                text: $"❌ Ошибка: {order.Error}");
//        }
//    }

//    // Получение открытых позиций
//    private static async Task<List<BinancePosition>> GetOpenPositions(BinanceRestClient client)
//    {
//        var result = new List<BinancePosition>();

//        var accountInfo = await client.SpotApi.Account.GetAccountInfoAsync();
//        if (!accountInfo.Success)
//        {
//            logger.LogError("Ошибка получения информации об аккаунте");
//            return result;
//        }

//        var symbolParts = config.Symbol.ToUpper().Split("USDT");
//        var baseAsset = symbolParts[0];

//        var baseBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == baseAsset)?.Total;
//        var quoteBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == "USDT")?.Total;

//        if (baseBalance > 0)
//        {
//            var ticker = await client.SpotApi.ExchangeData.GetPriceAsync(config.Symbol);
//            if (!ticker.Success)
//            {
//                logger.LogError("Ошибка получения цены для расчета позиции");
//                return result;
//            }

//            result.Add(new BinancePosition
//            {
//                Symbol = config.Symbol,
//                PositionAmount = baseBalance.Value,
//                EntryPrice = 0, // В спотовой торговле Binance не предоставляет цену входа
//                MarkPrice = ticker.Data.Price,
//                Side = PositionSide.Long
//            });
//        }

//        return result;
//    }

//    // Расчет SMA (простое скользящее среднее)
//    private static double CalculateSma(double[] closes, int period)
//    {
//        if (closes.Length < period)
//        {
//            logger.LogDebug($"Недостаточно данных для SMA{period}. Нужно: {period}, есть: {closes.Length}");
//            return 0;
//        }
//        return closes.TakeLast(period).Average();
//    }

//    // Расчет RSI (индекс относительной силы)
//    private static double CalculateRsi(double[] closes, int period)
//    {
//        if (closes.Length <= period)
//        {
//            logger.LogDebug($"Недостаточно данных для RSI{period}. Нужно: {period + 1}, есть: {closes.Length}");
//            return 50;
//        }

//        var deltas = new double[closes.Length - 1];
//        for (int i = 1; i < closes.Length; i++)
//            deltas[i - 1] = closes[i] - closes[i - 1];

//        var gains = deltas.Where(d => d > 0).TakeLast(period).DefaultIfEmpty(0).Average();
//        var losses = Math.Abs(deltas.Where(d => d < 0).TakeLast(period).DefaultIfEmpty(0).Average());

//        if (losses == 0) return 100;
//        double rs = gains / losses;
//        return 100 - (100 / (1 + rs));
//    }

//    // Расчет MACD

//    private static (double macdLine, double signalLine, double histogram) CalculateMacd(double[] closes, int fastPeriod, int slowPeriod, int signalPeriod)
//    {
//        // Если данных недостаточно для медленного периода, используем доступные данные
//        if (closes.Length < slowPeriod)
//        {
//            double lastValue = closes.Length > 0 ? closes.Last() : 0;
//            return (lastValue, lastValue, 0);
//        }

//        var fastEma = CalculateEma(closes, fastPeriod);
//        var slowEma = CalculateEma(closes, slowPeriod);
//        var macdLine = fastEma - slowEma;

//        // Для сигнальной линии нужно минимум signalPeriod значений MACD
//        if (closes.Length < slowPeriod + signalPeriod)
//        {
//            return (macdLine, macdLine, 0);
//        }

//        var signalLine = CalculateEma(closes.TakeLast(signalPeriod * 2).Select((x, i) =>
//            CalculateEma(closes.Take(i + 1).ToArray(), fastPeriod) -
//            CalculateEma(closes.Take(i + 1).ToArray(), slowPeriod)).ToArray(), signalPeriod);

//        var histogram = macdLine - signalLine;

//        return (macdLine, signalLine, histogram);
//    }

//    // Расчет EMA (экспоненциальное скользящее среднее)
//    private static double CalculateEma(double[] closes, int period, double? prevEma = null)
//    {
//        if (closes.Length < period)
//        {
//            // Вместо возврата 0 возвращаем последнее известное значение
//            return closes.Length > 0 ? closes.Last() : 0;
//        }

//        double k = 2.0 / (period + 1);
//        double ema = prevEma ?? closes.Take(period).Average();

//        for (int i = period; i < closes.Length; i++)
//        {
//            ema = closes[i] * k + ema * (1 - k);
//        }

//        return ema;
//    }

//    // Расчет полос Боллинджера
//    private static (double upperBand, double middleBand, double lowerBand) CalculateBollingerBands(double[] closes, int period, double stdDevMultiplier)
//    {
//        if (closes.Length < period)
//        {
//            logger.LogDebug($"Недостаточно данных для BB{period}. Нужно: {period}, есть: {closes.Length}");
//            return (0, 0, 0);
//        }

//        var relevantCloses = closes.TakeLast(period).ToArray();
//        var sma = relevantCloses.Average();
//        var stdDev = Math.Sqrt(relevantCloses.Sum(x => Math.Pow(x - sma, 2)) / period);

//        return (sma + stdDev * stdDevMultiplier, sma, sma - stdDev * stdDevMultiplier);
//    }

//    // Сохранение истории сделок
//    private static void SaveTradeHistory(List<TradeRecord> history, string prefix = "TradeHistory")
//    {
//        try
//        {
//            string fileName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
//            using var writer = new StreamWriter(fileName);

//            writer.WriteLine("Timestamp,Type,Quantity,EntryPrice,ExitPrice,StopLoss,TakeProfit,PnL,Notes");

//            foreach (var trade in history)
//            {
//                writer.WriteLine($"{trade.Timestamp:yyyy-MM-dd HH:mm:ss},{trade.Type}," +
//                                $"{trade.Quantity:F6},{trade.EntryPrice:F2},{trade.ExitPrice:F2}," +
//                                $"{trade.StopLossPrice:F2},{trade.TakeProfitPrice:F2},{trade.PnL:F2}," +
//                                $"\"{trade.Notes}\"");
//            }

//            logger.LogInformation("История сделок сохранена в файл: {FileName}", fileName);
//        }
//        catch (Exception ex)
//        {
//            logger.LogError(ex, "Ошибка при сохранении истории сделок");
//        }
//    }

//    // Расчет максимальной просадки
//    private static decimal CalculateMaxDrawdown(List<decimal> equityCurve)
//    {
//        if (equityCurve.Count == 0) return 0;

//        decimal peak = equityCurve[0];
//        decimal maxDrawdown = 0;

//        foreach (var value in equityCurve)
//        {
//            if (value > peak) peak = value;
//            decimal drawdown = (peak - value) / peak * 100;
//            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
//        }

//        return maxDrawdown;
//    }

//    // Класс для представления позиции
//    public class BinancePosition
//    {
//        public string Symbol { get; set; }
//        public decimal PositionAmount { get; set; }
//        public decimal EntryPrice { get; set; }
//        public decimal MarkPrice { get; set; }
//        public decimal UnrealizedPnl { get; set; }
//        public PositionSide Side { get; set; }
//    }

//    // Сторона позиции
//    public enum PositionSide
//    {
//        Long,
//        Short
//    }
//}