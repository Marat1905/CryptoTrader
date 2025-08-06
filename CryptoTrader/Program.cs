using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Logging;
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
        public decimal RiskPerTrade { get; set; } = 0.02m; // 2% риска на сделку
        public decimal StopLossPercent { get; set; } = 0.05m; // 5% стоп-лосс
        public decimal MaxDailyLossPercent { get; set; } = 0.10m; // 10% макс. убыток за день
        public decimal TakeProfitPercent { get; set; } = 0.10m; // 10% тейк-профит

        // Параметры для оптимизации
        public int[] FastMAPeriodRange { get; set; } = new[] { 5, 15 };
        public int[] SlowMAPeriodRange { get; set; } = new[] { 15, 50 };
        public int[] RSIPeriodRange { get; set; } = new[] { 10, 20 };
        public double[] OverboughtLevelRange { get; set; } = new[] { 60.0, 80.0 };
        public double[] OversoldLevelRange { get; set; } = new[] { 20.0, 40.0 };

        // Фильтры по объему и волатильности
        public decimal MinVolumeUSDT { get; set; } = 1000000m; // Минимальный объем для торговли (1M USDT)
        public decimal VolumeChangeThreshold { get; set; } = 0.5m; // 50% изменение объема
        public decimal VolatilityThreshold { get; set; } = 0.02m; // 2% волатильность
        public int VolatilityPeriod { get; set; } = 14; // Период для расчета волатильности

        // Мультитаймфреймовый анализ
        public KlineInterval PrimaryTimeframe { get; set; } = KlineInterval.OneHour;
        public KlineInterval HigherTimeframe { get; set; } = KlineInterval.FourHour;
        public KlineInterval LowerTimeframe { get; set; } = KlineInterval.FifteenMinutes;

        // Параметры по умолчанию
        public int FastMAPeriod { get; set; } = 9;
        public int SlowMAPeriod { get; set; } = 21;
        public int RSIPeriod { get; set; } = 14;
        public double OverboughtLevel { get; set; } = 70.0;
        public double OversoldLevel { get; set; } = 30.0;

        // Параметры MACD
        public int FastEmaPeriod { get; set; } = 12;
        public int SlowEmaPeriod { get; set; } = 26;
        public int SignalPeriod { get; set; } = 9;

        // Параметры Bollinger Bands
        public int BbPeriod { get; set; } = 20;
        public double BbStdDev { get; set; } = 2.0;

        public int OptimizationGenerations { get; set; } = 10;
        public int OptimizationPopulationSize { get; set; } = 20;
        public int CheckIntervalMinutes { get; set; } = 1;
        public bool BacktestMode { get; set; } = true;
        public bool OptimizeMode { get; set; } = true;
        public DateTime BacktestStartDate { get; set; } = new DateTime(2023, 1, 1);
        public DateTime BacktestEndDate { get; set; } = DateTime.Now;
        public KlineInterval BacktestInterval { get; set; } = KlineInterval.OneHour;
        public decimal InitialBalance { get; set; } = 1000m;
    }

    public record TradingParams(
        int FastMAPeriod,
        int SlowMAPeriod,
        int RSIPeriod,
        double OverboughtLevel,
        double OversoldLevel,
        int FastEmaPeriod,
        int SlowEmaPeriod,
        int SignalPeriod,
        int BbPeriod,
        double BbStdDev)
    {
        public override string ToString() =>
            $"FastMA={FastMAPeriod}, SlowMA={SlowMAPeriod}, RSI={RSIPeriod}, OB={OverboughtLevel:F1}, OS={OversoldLevel:F1}, " +
            $"MACD(F={FastEmaPeriod},S={SlowEmaPeriod},Sig={SignalPeriod}), BB(P={BbPeriod},SD={BbStdDev:F1})";
    }

    public record TradeRecord(
        DateTime Timestamp,
        string Type,
        decimal Quantity,
        decimal EntryPrice,
        decimal ExitPrice,
        decimal StopLossPrice,
        decimal TakeProfitPrice,
        decimal PnL)
    {
        public bool IsClosed => ExitPrice != 0;
    }

    public class OptimizationDataPoint
    {
        public TradingParams Parameters { get; }
        public double Score { get; }

        public OptimizationDataPoint(TradingParams parameters, double score)
        {
            Parameters = parameters;
            Score = score;
        }
    }

    private static BotConfig config = new BotConfig();
    private static ILogger logger;
    private static decimal dailyPnL = 0;
    private static DateTime lastTradeDate = DateTime.MinValue;

    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        logger = loggerFactory.CreateLogger("CryptoBot");

        var binanceClient = new BinanceRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(config.ApiKey, config.ApiSecret);
        });

        var telegramBot = new TelegramBotClient(config.TelegramToken);

        // Бактест с параметрами по умолчанию перед оптимизацией
        if (config.BacktestMode)
        {
            logger.LogInformation("=== БАКТЕСТ С ПАРАМЕТРАМИ ПО УМОЛЧАНИЮ ===");
            await RunBacktest(binanceClient, telegramBot, "с параметрами по умолчанию", new TradingParams(
                config.FastMAPeriod,
                config.SlowMAPeriod,
                config.RSIPeriod,
                config.OverboughtLevel,
                config.OversoldLevel,
                config.FastEmaPeriod,
                config.SlowEmaPeriod,
                config.SignalPeriod,
                config.BbPeriod,
                config.BbStdDev));
        }

        if (config.OptimizeMode)
        {
            await OptimizeParameters(binanceClient, telegramBot);
        }

        if (config.BacktestMode)
        {
            logger.LogInformation("=== БАКТЕСТ С ОПТИМИЗИРОВАННЫМИ ПАРАМЕТРАМИ ===");
            await RunBacktest(binanceClient, telegramBot, "c оптимизированными параметрами");
        }
        else
        {
            logger.LogInformation("Бот запущен в реальном режиме. Мониторинг рынка...");
            await RunLiveTrading(binanceClient, telegramBot);
        }
    }

    private static async Task OptimizeParameters(BinanceRestClient binanceClient, TelegramBotClient telegramBot)
    {
        logger.LogInformation("Запуск оптимизации параметров...");

        var allKlines = await GetAllHistoricalData(binanceClient);
        if (allKlines == null || !allKlines.Any()) return;

        // Бактест перед оптимизацией
        logger.LogInformation("=== БАКТЕСТ ПЕРЕД ОПТИМИЗАЦИЕЙ ===");
        var defaultParams = new TradingParams(
            config.FastMAPeriod,
            config.SlowMAPeriod,
            config.RSIPeriod,
            config.OverboughtLevel,
            config.OversoldLevel,
            config.FastEmaPeriod,
            config.SlowEmaPeriod,
            config.SignalPeriod,
            config.BbPeriod,
            config.BbStdDev);
        var defaultScore = EvaluateParameters(allKlines, defaultParams);
        logger.LogInformation($"Результат до оптимизации: {defaultScore:F2} с параметрами: {defaultParams}");

        var random = new Random();
        var bestScore = double.MinValue;
        var bestParams = defaultParams;

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
                        random.Next(10, 20), // Fast EMA
                        random.Next(20, 30), // Slow EMA
                        random.Next(5, 15),  // Signal
                        random.Next(15, 25), // BB Period
                        1.5 + random.NextDouble() * 1.5)); // BB StdDev (1.5-3.0)
                }
            }
            else
            {
                for (int i = 0; i < config.OptimizationPopulationSize; i++)
                {
                    population.Add(MutateParams(bestParams, random));
                }
            }

            foreach (var paramSet in population)
            {
                var result = EvaluateParameters(allKlines, paramSet);

                if (result > bestScore)
                {
                    bestScore = result;
                    bestParams = paramSet;
                    logger.LogInformation($"Новый лучший результат: {bestScore:F2} с параметрами: {bestParams}");
                }
            }
        }

        // Бактест после оптимизации
        logger.LogInformation("=== БАКТЕСТ ПОСЛЕ ОПТИМИЗАЦИИ ===");
        var optimizedScore = EvaluateParameters(allKlines, bestParams);
        logger.LogInformation($"Результат после оптимизации: {optimizedScore:F2} с параметрами: {bestParams}");

        logger.LogInformation("\n=== РЕЗУЛЬТАТЫ ОПТИМИЗАЦИИ ===");
        logger.LogInformation($"Результат до оптимизации: {defaultScore:F2}");
        logger.LogInformation($"Лучший результат: {bestScore:F2}");
        logger.LogInformation($"Лучшие параметры: {bestParams}");

        await telegramBot.SendMessage(
            chatId: config.TelegramChatId,
            text: $"🎯 Результаты оптимизации {config.Symbol}\n" +
                  $"До оптимизации: {defaultScore:F2}\n" +
                  $"После оптимизации: {bestScore:F2}\n" +
                  $"Параметры: {bestParams}");

        // Обновляем параметры конфига
        config.FastMAPeriod = bestParams.FastMAPeriod;
        config.SlowMAPeriod = bestParams.SlowMAPeriod;
        config.RSIPeriod = bestParams.RSIPeriod;
        config.OverboughtLevel = bestParams.OverboughtLevel;
        config.OversoldLevel = bestParams.OversoldLevel;
        config.FastEmaPeriod = bestParams.FastEmaPeriod;
        config.SlowEmaPeriod = bestParams.SlowEmaPeriod;
        config.SignalPeriod = bestParams.SignalPeriod;
        config.BbPeriod = bestParams.BbPeriod;
        config.BbStdDev = bestParams.BbStdDev;
    }

    private static TradingParams MutateParams(TradingParams bestParams, Random random)
    {
        return new TradingParams(
            MutateValue(bestParams.FastMAPeriod, config.FastMAPeriodRange[0], config.FastMAPeriodRange[1], random),
            MutateValue(bestParams.SlowMAPeriod, config.SlowMAPeriodRange[0], config.SlowMAPeriodRange[1], random),
            MutateValue(bestParams.RSIPeriod, config.RSIPeriodRange[0], config.RSIPeriodRange[1], random),
            MutateValue(bestParams.OverboughtLevel, config.OverboughtLevelRange[0], config.OverboughtLevelRange[1], random),
            MutateValue(bestParams.OversoldLevel, config.OversoldLevelRange[0], config.OversoldLevelRange[1], random),
            MutateValue(bestParams.FastEmaPeriod, 8, 20, random),
            MutateValue(bestParams.SlowEmaPeriod, 20, 35, random),
            MutateValue(bestParams.SignalPeriod, 5, 15, random),
            MutateValue(bestParams.BbPeriod, 10, 30, random),
            Math.Max(1.0, Math.Min(3.0, bestParams.BbStdDev + (random.NextDouble() - 0.5) * 0.5)));
    }

    private static T MutateValue<T>(T value, T min, T max, Random random) where T : struct
    {
        if (random.NextDouble() >= 0.3)
            return value;

        if (typeof(T) == typeof(int))
        {
            int val = (int)(object)value;
            int minVal = (int)(object)min;
            int maxVal = (int)(object)max;
            int change = random.Next(-2, 3);
            int newValue = val + change;
            return (T)(object)Math.Min(maxVal, Math.Max(minVal, newValue));
        }
        else if (typeof(T) == typeof(double))
        {
            double val = (double)(object)value;
            double minVal = (double)(object)min;
            double maxVal = (double)(object)max;
            double change = (random.NextDouble() - 0.5) * 4;
            double newValue = val + change;
            return (T)(object)Math.Min(maxVal, Math.Max(minVal, newValue));
        }

        throw new NotSupportedException($"Тип {typeof(T)} не поддерживается");
    }

    private static double EvaluateParameters(List<IBinanceKline> allKlines, TradingParams parameters)
    {
        decimal balance = config.InitialBalance;
        decimal position = 0;
        decimal entryPrice = 0;
        decimal stopLossPrice = 0;
        decimal takeProfitPrice = 0;
        var equityCurve = new List<decimal>();

        for (int i = Math.Max(Math.Max(parameters.SlowMAPeriod, parameters.RSIPeriod), parameters.BbPeriod); i < allKlines.Count; i++)
        {
            var currentKline = allKlines[i];
            var previousKlines = allKlines.Take(i).ToList();

            // Проверка объема
            if (!CheckVolumeFilter(previousKlines, i)) continue;

            // Проверка волатильности
            if (!CheckVolatilityFilter(previousKlines, i)) continue;

            var closePrices = previousKlines.Select(k => (double)k.ClosePrice).ToArray();

            // Рассчитываем все индикаторы
            var fastMa = CalculateSma(closePrices, parameters.FastMAPeriod);
            var slowMa = CalculateSma(closePrices, parameters.SlowMAPeriod);
            var rsi = CalculateRsi(closePrices, parameters.RSIPeriod);
            var (macdLine, signalLine, _) = CalculateMacd(closePrices, parameters.FastEmaPeriod, parameters.SlowEmaPeriod, parameters.SignalPeriod);
            var (upperBand, middleBand, lowerBand) = CalculateBollingerBands(closePrices, parameters.BbPeriod, parameters.BbStdDev);
            var currentPrice = (double)currentKline.ClosePrice;

            // Проверка стоп-лосса и тейк-профита
            if (position != 0)
            {
                if (position > 0 && (decimal)currentPrice <= stopLossPrice)
                {
                    var pnl = position * (stopLossPrice - entryPrice);
                    balance += pnl;
                    position = 0;
                    equityCurve.Add(balance);
                    continue;
                }
                else if (position > 0 && (decimal)currentPrice >= takeProfitPrice)
                {
                    var pnl = position * (takeProfitPrice - entryPrice);
                    balance += pnl;
                    position = 0;
                    equityCurve.Add(balance);
                    continue;
                }
                else if (position < 0 && (decimal)currentPrice >= stopLossPrice)
                {
                    var pnl = position * (entryPrice - stopLossPrice);
                    balance += pnl;
                    position = 0;
                    equityCurve.Add(balance);
                    continue;
                }
                else if (position < 0 && (decimal)currentPrice <= takeProfitPrice)
                {
                    var pnl = position * (entryPrice - takeProfitPrice);
                    balance += pnl;
                    position = 0;
                    equityCurve.Add(balance);
                    continue;
                }
            }

            // Комплексные условия входа с использованием всех индикаторов
            bool isBullish = fastMa > slowMa &&
                           closePrices[^2] <= slowMa &&
                           rsi < parameters.OverboughtLevel &&
                           macdLine > signalLine &&
                           currentPrice < lowerBand;

            bool isBearish = fastMa < slowMa &&
                            closePrices[^2] >= slowMa &&
                            rsi > parameters.OversoldLevel &&
                            macdLine < signalLine &&
                            currentPrice > upperBand;

            if (isBullish && position <= 0)
            {
                if (position < 0)
                {
                    var pnl = position * ((decimal)currentPrice - entryPrice);
                    balance += pnl;
                }

                decimal quantity = (balance * config.RiskPerTrade) / (decimal)currentPrice;
                position = quantity;
                entryPrice = (decimal)currentPrice;
                stopLossPrice = entryPrice * (1 - config.StopLossPercent);
                takeProfitPrice = entryPrice * (1 + config.TakeProfitPercent);
            }
            else if (isBearish && position >= 0)
            {
                if (position > 0)
                {
                    var pnl = position * ((decimal)currentPrice - entryPrice);
                    balance += pnl;
                }

                decimal quantity = (balance * config.RiskPerTrade) / (decimal)currentPrice;
                position = -quantity;
                entryPrice = (decimal)currentPrice;
                stopLossPrice = entryPrice * (1 + config.StopLossPercent);
                takeProfitPrice = entryPrice * (1 - config.TakeProfitPercent);
            }

            equityCurve.Add(balance + position * ((decimal)currentPrice - entryPrice));
        }

        if (position != 0)
        {
            var lastPrice = (double)allKlines.Last().ClosePrice;
            var pnl = position * ((decimal)lastPrice - entryPrice);
            balance += pnl;
        }

        double profitRatio = (double)(balance / config.InitialBalance);
        double sharpeRatio = CalculateSharpeRatio(equityCurve);

        return profitRatio * 0.7 + sharpeRatio * 0.3;
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

        if (currentKline.Volume * currentKline.ClosePrice < config.MinVolumeUSDT)
            return false;

        if (prevKline.Volume == 0)
            return true;

        var volumeChange = Math.Abs((currentKline.Volume - prevKline.Volume) / prevKline.Volume);
        return volumeChange >= config.VolumeChangeThreshold;
    }

    private static bool CheckVolatilityFilter(List<IBinanceKline> klines, int currentIndex)
    {
        if (currentIndex < config.VolatilityPeriod) return true;

        var relevantKlines = klines.Skip(currentIndex - config.VolatilityPeriod).Take(config.VolatilityPeriod).ToList();
        var atr = CalculateATR(relevantKlines, config.VolatilityPeriod);

        var currentPrice = klines[currentIndex].ClosePrice;
        var volatility = atr / currentPrice;

        return volatility >= config.VolatilityThreshold;
    }

    private static decimal CalculateATR(List<IBinanceKline> klines, int period)
    {
        var trueRanges = new List<double>();

        for (int i = 1; i < klines.Count; i++)
        {
            var current = klines[i];
            var previous = klines[i - 1];

            double highLow = (double)(current.HighPrice - current.LowPrice);
            double highClose = Math.Abs((double)(current.HighPrice - previous.ClosePrice));
            double lowClose = Math.Abs((double)(current.LowPrice - previous.ClosePrice));

            trueRanges.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
        }

        if (trueRanges.Count < period) return 0;
        return (decimal)trueRanges.TakeLast(period).Average();
    }

    private static double CalculateSharpeRatio(List<decimal> equityCurve)
    {
        if (equityCurve.Count < 2) return 0;

        var dailyReturns = new List<double>();
        for (int i = 1; i < equityCurve.Count; i++)
        {
            double ret = (double)((equityCurve[i] - equityCurve[i - 1]) / equityCurve[i - 1]);
            dailyReturns.Add(ret);
        }

        if (!dailyReturns.Any()) return 0;

        double avgReturn = dailyReturns.Average();
        double stdDev = Math.Sqrt(dailyReturns.Sum(r => Math.Pow(r - avgReturn, 2)) / dailyReturns.Count);

        if (stdDev == 0) return 0;
        return avgReturn / stdDev * Math.Sqrt(365);
    }

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

                await Task.Delay(250);
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

    private static async Task RunBacktest(BinanceRestClient binanceClient, TelegramBotClient telegramBot, string text, TradingParams parameters = null)
    {
        try
        {
            parameters ??= new TradingParams(
                config.FastMAPeriod,
                config.SlowMAPeriod,
                config.RSIPeriod,
                config.OverboughtLevel,
                config.OversoldLevel,
                config.FastEmaPeriod,
                config.SlowEmaPeriod,
                config.SignalPeriod,
                config.BbPeriod,
                config.BbStdDev);

            logger.LogInformation("=== НАЧАЛО БЭКТЕСТА ===");
            logger.LogInformation($"Параметры: {parameters}");
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

            logger.LogInformation($"Получено {allKlines.Count} свечей");
            logger.LogInformation($"Пример данных: Первая свеча - {allKlines.First().OpenTime}, Последняя - {allKlines.Last().OpenTime}");

            decimal balance = config.InitialBalance;
            decimal position = 0;
            decimal entryPrice = 0;
            var tradeHistory = new List<TradeRecord>();
            var equityCurve = new List<decimal> { balance };
            int signalsGenerated = 0;
            int tradesExecuted = 0;

            int requiredBars = new[] { parameters.SlowMAPeriod, parameters.RSIPeriod, parameters.BbPeriod, config.VolatilityPeriod }.Max() + 1;

            if (allKlines.Count < requiredBars)
            {
                logger.LogError($"Недостаточно данных. Требуется: {requiredBars}, получено: {allKlines.Count}");
                return;
            }

            logger.LogInformation("Начало обработки данных...");
            for (int i = requiredBars; i < allKlines.Count; i++)
            {
                var currentKline = allKlines[i];
                var previousKlines = allKlines.Take(i).ToList();
                var closePrices = previousKlines.Select(k => (double)k.ClosePrice).ToArray();
                var currentPrice = (double)currentKline.ClosePrice;

                // Рассчитываем все индикаторы
                var fastMa = CalculateSma(closePrices, parameters.FastMAPeriod);
                var slowMa = CalculateSma(closePrices, parameters.SlowMAPeriod);
                var rsi = CalculateRsi(closePrices, parameters.RSIPeriod);
                var (macdLine, signalLine, _) = CalculateMacd(closePrices, parameters.FastEmaPeriod, parameters.SlowEmaPeriod, parameters.SignalPeriod);
                var (upperBand, middleBand, lowerBand) = CalculateBollingerBands(closePrices, parameters.BbPeriod, parameters.BbStdDev);

                if (i % 50 == 0)
                {
                    logger.LogInformation($"Свеча {i}: Time={currentKline.OpenTime}, Price={currentPrice:F2}, " +
                        $"MA{parameters.FastMAPeriod}={fastMa:F2}, MA{parameters.SlowMAPeriod}={slowMa:F2}, " +
                        $"RSI={rsi:F2}, MACD={macdLine:F2}/{signalLine:F2}, " +
                        $"BB={lowerBand:F2}/{middleBand:F2}/{upperBand:F2}");
                }

                // Комплексные условия для входа
                bool isBullish = fastMa > slowMa &&
                               closePrices[^2] <= slowMa &&
                               rsi < parameters.OverboughtLevel &&
                               macdLine > signalLine &&
                               currentPrice < lowerBand;

                bool isBearish = fastMa < slowMa &&
                                closePrices[^2] >= slowMa &&
                                rsi > parameters.OversoldLevel &&
                                macdLine < signalLine &&
                                currentPrice > upperBand;

                // Обработка открытых позиций
                if (position != 0)
                {
                    bool shouldClose = false;
                    decimal exitPrice = 0;
                    string exitReason = "";

                    if (position > 0) // Длинная позиция
                    {
                        if ((decimal)currentPrice <= entryPrice * (1m - config.StopLossPercent))
                        {
                            exitPrice = entryPrice * (1m - config.StopLossPercent);
                            exitReason = "SL";
                            shouldClose = true;
                        }
                        else if ((decimal)currentPrice >= entryPrice * (1m + config.TakeProfitPercent))
                        {
                            exitPrice = entryPrice * (1m + config.TakeProfitPercent);
                            exitReason = "TP";
                            shouldClose = true;
                        }
                    }
                    else // Короткая позиция
                    {
                        if ((decimal)currentPrice >= entryPrice * (1m + config.StopLossPercent))
                        {
                            exitPrice = entryPrice * (1m + config.StopLossPercent);
                            exitReason = "SL";
                            shouldClose = true;
                        }
                        else if ((decimal)currentPrice <= entryPrice * (1m - config.TakeProfitPercent))
                        {
                            exitPrice = entryPrice * (1m - config.TakeProfitPercent);
                            exitReason = "TP";
                            shouldClose = true;
                        }
                    }

                    if (shouldClose)
                    {
                        decimal pnl = position > 0
                            ? position * (exitPrice - entryPrice)
                            : position * (entryPrice - exitPrice);

                        balance += pnl;
                        tradesExecuted++;

                        tradeHistory.Add(new TradeRecord(
                            currentKline.OpenTime,
                            position > 0 ? $"SELL ({exitReason})" : $"BUY ({exitReason})",
                            Math.Abs(position),
                            entryPrice,
                            exitPrice,
                            position > 0 ? entryPrice * (1m - config.StopLossPercent) : entryPrice * (1m + config.StopLossPercent),
                            position > 0 ? entryPrice * (1m + config.TakeProfitPercent) : entryPrice * (1m - config.TakeProfitPercent),
                            pnl));

                        position = 0;
                        equityCurve.Add(balance);
                        continue;
                    }
                }

                // Генерация новых сигналов
                if (isBullish && position <= 0)
                {
                    signalsGenerated++;

                    if (position < 0)
                    {
                        decimal pnl = position * ((decimal)currentPrice - entryPrice);
                        balance += pnl;
                        tradesExecuted++;

                        tradeHistory.Add(new TradeRecord(
                            currentKline.OpenTime,
                            "BUY (Close)",
                            Math.Abs(position),
                            entryPrice,
                            (decimal)currentPrice,
                            0, 0, pnl));
                    }

                    decimal quantity = (balance * config.RiskPerTrade) / (decimal)currentPrice;
                    position = quantity;
                    entryPrice = (decimal)currentPrice;

                    tradeHistory.Add(new TradeRecord(
                        currentKline.OpenTime,
                        "BUY",
                        quantity,
                        entryPrice,
                        0,
                        entryPrice * (1m - config.StopLossPercent),
                        entryPrice * (1m + config.TakeProfitPercent),
                        0));
                }
                else if (isBearish && position >= 0)
                {
                    signalsGenerated++;

                    if (position > 0)
                    {
                        decimal pnl = position * ((decimal)currentPrice - entryPrice);
                        balance += pnl;
                        tradesExecuted++;

                        tradeHistory.Add(new TradeRecord(
                            currentKline.OpenTime,
                            "SELL (Close)",
                            position,
                            entryPrice,
                            (decimal)currentPrice,
                            0, 0, pnl));
                    }

                    decimal quantity = (balance * config.RiskPerTrade) / (decimal)currentPrice;
                    position = -quantity;
                    entryPrice = (decimal)currentPrice;

                    tradeHistory.Add(new TradeRecord(
                        currentKline.OpenTime,
                        "SELL",
                        quantity,
                        entryPrice,
                        0,
                        entryPrice * (1m + config.StopLossPercent),
                        entryPrice * (1m - config.TakeProfitPercent),
                        0));
                }

                equityCurve.Add(balance + position * ((decimal)currentPrice - entryPrice));
            }

            if (position != 0)
            {
                var lastPrice = (double)allKlines.Last().ClosePrice;
                decimal pnl = position * ((decimal)lastPrice - entryPrice);
                balance += pnl;
                tradesExecuted++;

                tradeHistory.Add(new TradeRecord(
                    allKlines.Last().OpenTime,
                    position > 0 ? "SELL (Close)" : "BUY (Close)",
                    Math.Abs(position),
                    entryPrice,
                    (decimal)lastPrice,
                    0, 0, pnl));
            }

            decimal profit = balance - config.InitialBalance;
            decimal profitPercentage = (balance / config.InitialBalance - 1) * 100;
            decimal winRate = tradeHistory.Count(t => t.PnL > 0) * 100m / Math.Max(1, tradeHistory.Count);
            decimal maxDrawdown = CalculateMaxDrawdown(equityCurve);
            int totalTrades = tradeHistory.Count(t => t.IsClosed);

            logger.LogInformation("\n=== РЕЗУЛЬТАТЫ БЭКТЕСТА ===");
            logger.LogInformation($"Сигналов сгенерировано: {signalsGenerated}");
            logger.LogInformation($"Сделок выполнено: {tradesExecuted}");
            logger.LogInformation($"Конечный баланс: {balance:F2}");
            logger.LogInformation($"Прибыль: {profit:F2} ({profitPercentage:F2}%)");
            logger.LogInformation($"Процент прибыльных сделок: {winRate:F2}%");
            logger.LogInformation($"Максимальная просадка: {maxDrawdown:F2}%");

            var message = $"📊 Результаты бэктеста {text}: {config.Symbol}\n" +
                         $"Период: {config.BacktestStartDate:dd.MM.yyyy} - {config.BacktestEndDate:dd.MM.yyyy}\n" +
                         $"Таймфрейм: {config.BacktestInterval}\n" +
                         $"Баланс: {config.InitialBalance:F2} → {balance:F2}\n" +
                         $"Прибыль: {profit:F2} ({profitPercentage:F2}%)\n" +
                         $"Сделок: {totalTrades} | Прибыльных: {winRate:F2}%\n" +
                         $"Просадка: {maxDrawdown:F2}%";

            await telegramBot.SendMessage(config.TelegramChatId, message);

            SaveTradeHistory(tradeHistory);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка в RunBacktest");
            await telegramBot.SendMessage(config.TelegramChatId,
                $"❌ Ошибка при выполнении бэктеста: {ex.Message}");
        }
    }

    private static void SaveTradeHistory(List<TradeRecord> history)
    {
        try
        {
            string fileName = $"TradeHistory_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            using var writer = new StreamWriter(fileName);

            writer.WriteLine("Timestamp,Type,Quantity,EntryPrice,ExitPrice,StopLoss,TakeProfit,PnL");

            foreach (var trade in history)
            {
                writer.WriteLine($"{trade.Timestamp:yyyy-MM-dd HH:mm:ss},{trade.Type}," +
                                $"{trade.Quantity:F6},{trade.EntryPrice:F2},{trade.ExitPrice:F2}," +
                                $"{trade.StopLossPrice:F2},{trade.TakeProfitPrice:F2},{trade.PnL:F2}");
            }

            logger.LogInformation("История сделок сохранена в файл: {FileName}", fileName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка при сохранении истории сделок");
        }
    }

    private static decimal CalculateMaxDrawdown(List<decimal> equityCurve)
    {
        decimal peak = equityCurve[0];
        decimal maxDrawdown = 0;

        foreach (var value in equityCurve)
        {
            if (value > peak) peak = value;
            decimal drawdown = (peak - value) / peak * 100;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;
        }

        return maxDrawdown;
    }

    private static async Task RunLiveTrading(BinanceRestClient binanceClient, TelegramBotClient telegramBot)
    {
        while (true)
        {
            try
            {
                await CheckMarketAndTradeAsync(binanceClient, telegramBot);
                await Task.Delay(TimeSpan.FromMinutes(config.CheckIntervalMinutes));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка в основном цикле");
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
    }

    private static async Task CheckMarketAndTradeAsync(BinanceRestClient binanceClient, TelegramBotClient telegramBot)
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
            logger.LogInformation("Фильтр объема не пройден");
            return;
        }

        if (!CheckLiveVolatilityFilter(primaryKlines))
        {
            logger.LogInformation("Фильтр волатильности не пройден");
            return;
        }

        // Анализ на основном таймфрейме
        var primaryCloses = primaryKlines.Select(k => (double)k.ClosePrice).ToArray();
        var primaryFastMa = CalculateSma(primaryCloses, config.FastMAPeriod);
        var primarySlowMa = CalculateSma(primaryCloses, config.SlowMAPeriod);
        var primaryRsi = CalculateRsi(primaryCloses, config.RSIPeriod);
        var (primaryMacdLine, primarySignalLine, _) = CalculateMacd(primaryCloses, config.FastEmaPeriod, config.SlowEmaPeriod, config.SignalPeriod);
        var (primaryUpperBb, primaryMiddleBb, primaryLowerBb) = CalculateBollingerBands(primaryCloses, config.BbPeriod, config.BbStdDev);

        // Анализ на старшем таймфрейме (тренд)
        var higherCloses = higherKlines.Select(k => (double)k.ClosePrice).ToArray();
        var higherFastMa = CalculateSma(higherCloses, config.FastMAPeriod);
        var higherSlowMa = CalculateSma(higherCloses, config.SlowMAPeriod);

        // Анализ на младшем таймфрейме (точки входа)
        var lowerCloses = lowerKlines.Select(k => (double)k.ClosePrice).ToArray();
        var lowerFastMa = CalculateSma(lowerCloses, config.FastMAPeriod / 2);
        var lowerSlowMa = CalculateSma(lowerCloses, config.SlowMAPeriod / 2);
        var lowerRsi = CalculateRsi(lowerCloses, config.RSIPeriod / 2);
        var (lowerMacdLine, lowerSignalLine, _) = CalculateMacd(lowerCloses, config.FastEmaPeriod / 2, config.SlowEmaPeriod / 2, config.SignalPeriod / 2);
        var (lowerUpperBb, lowerMiddleBb, lowerLowerBb) = CalculateBollingerBands(lowerCloses, config.BbPeriod / 2, config.BbStdDev);

        var ticker = await binanceClient.SpotApi.ExchangeData.GetPriceAsync(config.Symbol);
        if (!ticker.Success)
        {
            logger.LogError("Ошибка получения цены: {Error}", ticker.Error);
            return;
        }
        var currentPrice = (double)ticker.Data.Price;

        logger.LogInformation(
            "{Time} | Цена: {Price} | MA{fastPeriod}: {FastMA} | MA{slowPeriod}: {SlowMA} | RSI: {RSI} | MACD: {MACD}/{Signal} | BB: {LowerBB}/{MiddleBB}/{UpperBB}",
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
            primaryUpperBb.ToString("F2"));

        // Определяем тренд на старшем таймфрейме
        bool isHigherTrendBullish = higherFastMa > higherSlowMa;
        bool isHigherTrendBearish = higherFastMa < higherSlowMa;

        // Сигналы на основном таймфрейме
        bool isPrimaryBullish = primaryFastMa > primarySlowMa &&
                              primaryCloses[^2] <= primarySlowMa &&
                              primaryRsi < config.OverboughtLevel &&
                              primaryMacdLine > primarySignalLine &&
                              currentPrice < primaryLowerBb;

        bool isPrimaryBearish = primaryFastMa < primarySlowMa &&
                               primaryCloses[^2] >= primarySlowMa &&
                               primaryRsi > config.OversoldLevel &&
                               primaryMacdLine < primarySignalLine &&
                               currentPrice > primaryUpperBb;

        // Сигналы на младшем таймфрейме для точного входа
        bool isLowerBullish = lowerFastMa > lowerSlowMa &&
                             lowerCloses[^2] <= lowerSlowMa &&
                             lowerRsi < config.OversoldLevel &&
                             lowerMacdLine > lowerSignalLine &&
                             currentPrice < lowerLowerBb;

        bool isLowerBearish = lowerFastMa < lowerSlowMa &&
                              lowerCloses[^2] >= lowerSlowMa &&
                              lowerRsi > config.OverboughtLevel &&
                              lowerMacdLine < lowerSignalLine &&
                              currentPrice > lowerUpperBb;

        // Комбинированные условия с мультитаймфреймовым анализом
        bool isBullish = (isHigherTrendBullish || !isHigherTrendBearish) && isPrimaryBullish && isLowerBullish;
        bool isBearish = (isHigherTrendBearish || !isHigherTrendBullish) && isPrimaryBearish && isLowerBearish;

        if (isBullish)
        {
            await ExecuteTradeAsync(binanceClient, telegramBot, OrderSide.Buy, (decimal)currentPrice);
        }
        else if (isBearish)
        {
            await ExecuteTradeAsync(binanceClient, telegramBot, OrderSide.Sell, (decimal)currentPrice);
        }
    }

    private static async Task<List<IBinanceKline>> GetKlinesForTimeframe(BinanceRestClient client, KlineInterval timeframe)
    {
        var klinesResult = await client.SpotApi.ExchangeData.GetKlinesAsync(
            config.Symbol,
            timeframe,
            limit: Math.Max(Math.Max(config.SlowMAPeriod, config.RSIPeriod), config.BbPeriod) + 50);

        if (!klinesResult.Success)
        {
            logger.LogError("Ошибка получения свечей для таймфрейма {0}: {1}", timeframe, klinesResult.Error);
            return null;
        }

        return klinesResult.Data.ToList();
    }

    private static bool CheckLiveVolumeFilter(List<IBinanceKline> klines)
    {
        if (klines.Count < 2) return false;

        var currentVolume = klines.Last().Volume;
        var prevVolume = klines[^2].Volume;

        if (currentVolume * klines.Last().ClosePrice < config.MinVolumeUSDT)
            return false;

        if (prevVolume == 0) return true;
        var volumeChange = Math.Abs((currentVolume - prevVolume) / prevVolume);

        return volumeChange >= config.VolumeChangeThreshold;
    }

    private static bool CheckLiveVolatilityFilter(List<IBinanceKline> klines)
    {
        if (klines.Count < config.VolatilityPeriod) return false;

        var relevantKlines = klines.TakeLast(config.VolatilityPeriod).ToList();
        var atr = CalculateATR(relevantKlines, config.VolatilityPeriod);

        var currentPrice = klines.Last().ClosePrice;
        var volatility = atr / currentPrice;

        return volatility >= config.VolatilityThreshold;
    }

    private static async Task ExecuteTradeAsync(BinanceRestClient binanceClient, TelegramBotClient telegramBot, OrderSide side, decimal currentPrice)
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

        decimal quantity = (usdtBalance.Value * config.RiskPerTrade) / currentPrice;
        quantity = Math.Round(quantity, 6);

        var order = await binanceClient.SpotApi.Trading.PlaceOrderAsync(
            config.Symbol,
            side,
            SpotOrderType.Market,
            quantity: quantity);

        if (order.Success)
        {
            var message = $"{(side == OrderSide.Buy ? "🟢 КУПЛЕНО" : "🔴 ПРОДАНО")} {quantity:0.000000} {config.Symbol} по {currentPrice:0.00}";
            logger.LogInformation(message);
            await telegramBot.SendMessage(
                chatId: config.TelegramChatId,
                text: message);

            decimal stopLossPrice = side == OrderSide.Buy
                ? currentPrice * (1 - config.StopLossPercent)
                : currentPrice * (1 + config.StopLossPercent);

            decimal takeProfitPrice = side == OrderSide.Buy
                ? currentPrice * (1 + config.TakeProfitPercent)
                : currentPrice * (1 - config.TakeProfitPercent);

            logger.LogInformation("Стоп-лосс: {0}, Тейк-профит: {1}",
                stopLossPrice.ToString("0.00"),
                takeProfitPrice.ToString("0.00"));

            if (!config.BacktestMode)
            {
                // Здесь можно разместить лимитные ордера или начать отслеживание цены
            }
        }
        else
        {
            logger.LogError("Ошибка ордера: {Error}", order.Error);
            await telegramBot.SendMessage(
                chatId: config.TelegramChatId,
                text: $"❌ Ошибка: {order.Error}");
        }
    }

    private static async Task<List<BinancePosition>> GetOpenPositions(BinanceRestClient client)
    {
        var result = new List<BinancePosition>();

        var accountInfo = await client.SpotApi.Account.GetAccountInfoAsync();
        if (!accountInfo.Success) return result;

        var ticker = await client.SpotApi.ExchangeData.GetPriceAsync(config.Symbol);
        if (!ticker.Success) return result;

        var symbolParts = config.Symbol.ToUpper().Split("USDT");
        var baseAsset = symbolParts[0];

        var baseBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == baseAsset)?.Total;
        var quoteBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == "USDT")?.Total;

        if (baseBalance > 0)
        {
            result.Add(new BinancePosition
            {
                Symbol = config.Symbol,
                PositionAmount = baseBalance.Value,
                EntryPrice = 0,
                MarkPrice = ticker.Data.Price,
                Side = PositionSide.Long
            });
        }

        return result;
    }

    // Методы для расчета индикаторов
    private static double CalculateSma(double[] closes, int period)
    {
        if (closes.Length < period) return 0;
        return closes.TakeLast(period).Average();
    }

    private static double CalculateRsi(double[] closes, int period)
    {
        if (closes.Length <= period) return 50;

        var deltas = new double[closes.Length - 1];
        for (int i = 1; i < closes.Length; i++)
            deltas[i - 1] = closes[i] - closes[i - 1];

        var gains = deltas.Where(d => d > 0).TakeLast(period).DefaultIfEmpty(0).Average();
        var losses = Math.Abs(deltas.Where(d => d < 0).TakeLast(period).DefaultIfEmpty(0).Average());

        if (losses == 0) return 100;
        double rs = gains / losses;
        return 100 - (100 / (1 + rs));
    }

    private static (double macdLine, double signalLine, double histogram) CalculateMacd(double[] closes, int fastPeriod, int slowPeriod, int signalPeriod)
    {
        if (closes.Length < slowPeriod + signalPeriod) return (0, 0, 0);

        var fastEma = CalculateEma(closes, fastPeriod);
        var slowEma = CalculateEma(closes, slowPeriod);
        var macdLine = fastEma - slowEma;

        // Для сигнальной линии используем EMA от MACD линии
        var signalLine = CalculateEma(closes.TakeLast(signalPeriod * 2).Select((x, i) =>
            CalculateEma(closes.Take(i + 1).ToArray(), fastPeriod) -
            CalculateEma(closes.Take(i + 1).ToArray(), slowPeriod)).ToArray(), signalPeriod);

        var histogram = macdLine - signalLine;

        return (macdLine, signalLine, histogram);
    }

    private static double CalculateEma(double[] closes, int period, double? prevEma = null)
    {
        if (closes.Length < period) return 0;

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
        if (closes.Length < period) return (0, 0, 0);

        var relevantCloses = closes.TakeLast(period).ToArray();
        var sma = relevantCloses.Average();
        var stdDev = Math.Sqrt(relevantCloses.Sum(x => Math.Pow(x - sma, 2)) / period);

        return (sma + stdDev * stdDevMultiplier, sma, sma - stdDev * stdDevMultiplier);
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