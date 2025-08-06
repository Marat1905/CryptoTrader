using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
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

        // Параметры для оптимизации
        public int[] FastMAPeriodRange { get; set; } = new[] { 5, 15 };
        public int[] SlowMAPeriodRange { get; set; } = new[] { 15, 50 };
        public int[] RSIPeriodRange { get; set; } = new[] { 10, 20 };
        public double[] OverboughtLevelRange { get; set; } = new[] { 60.0, 80.0 };
        public double[] OversoldLevelRange { get; set; } = new[] { 20.0, 40.0 };

        // Параметры по умолчанию
        public int FastMAPeriod { get; set; } = 9;
        public int SlowMAPeriod { get; set; } = 21;
        public int RSIPeriod { get; set; } = 14;
        public double OverboughtLevel { get; set; } = 70.0;
        public double OversoldLevel { get; set; } = 30.0;

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
        double OversoldLevel)
    {
        public override string ToString() =>
            $"FastMA={FastMAPeriod}, SlowMA={SlowMAPeriod}, RSI={RSIPeriod}, OB={OverboughtLevel:F1}, OS={OversoldLevel:F1}";
    }

    public record TradeRecord(
        DateTime Timestamp,
        string Type,
        decimal Quantity,
        decimal EntryPrice,
        decimal ExitPrice,
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
            await RunBacktest(binanceClient, telegramBot,"с параметрами по умолчанию", new TradingParams(
                config.FastMAPeriod,
                config.SlowMAPeriod,
                config.RSIPeriod,
                config.OverboughtLevel,
                config.OversoldLevel));
        }

        if (config.OptimizeMode)
        {
            await OptimizeParameters(binanceClient, telegramBot);
        }

        if (config.BacktestMode)
        {
            logger.LogInformation("=== БАКТЕСТ С ОПТИМИЗИРОВАННЫМИ ПАРАМЕТРАМИ ===");
            await RunBacktest(binanceClient, telegramBot,"c оптимизированными параметрами");
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
            config.OversoldLevel);
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
                        config.OverboughtLevelRange[0] + random.NextDouble() *
                            (config.OverboughtLevelRange[1] - config.OverboughtLevelRange[0]),
                        config.OversoldLevelRange[0] + random.NextDouble() *
                            (config.OversoldLevelRange[1] - config.OversoldLevelRange[0])));
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
    }

    private static TradingParams MutateParams(TradingParams bestParams, Random random)
    {
        return new TradingParams(
            MutateValue(bestParams.FastMAPeriod, config.FastMAPeriodRange[0], config.FastMAPeriodRange[1], random),
            MutateValue(bestParams.SlowMAPeriod, config.SlowMAPeriodRange[0], config.SlowMAPeriodRange[1], random),
            MutateValue(bestParams.RSIPeriod, config.RSIPeriodRange[0], config.RSIPeriodRange[1], random),
            MutateValue(bestParams.OverboughtLevel, config.OverboughtLevelRange[0], config.OverboughtLevelRange[1], random),
            MutateValue(bestParams.OversoldLevel, config.OversoldLevelRange[0], config.OversoldLevelRange[1], random));
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
        var equityCurve = new List<decimal>();

        for (int i = Math.Max(parameters.SlowMAPeriod, parameters.RSIPeriod); i < allKlines.Count; i++)
        {
            var currentKline = allKlines[i];
            var previousKlines = allKlines.Take(i).Select(k => (double)k.ClosePrice).ToArray();

            var fastMa = CalculateSma(previousKlines, parameters.FastMAPeriod);
            var slowMa = CalculateSma(previousKlines, parameters.SlowMAPeriod);
            var rsi = CalculateRsi(previousKlines, parameters.RSIPeriod);
            var currentPrice = (double)currentKline.ClosePrice;

            bool isBullish = fastMa > slowMa && previousKlines[^2] <= slowMa && rsi < parameters.OverboughtLevel;
            bool isBearish = fastMa < slowMa && previousKlines[^2] >= slowMa && rsi > parameters.OversoldLevel;

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

            if (!klinesResult.Data.Any()) break;

            allKlines.AddRange(klinesResult.Data);
            currentStartTime = klinesResult.Data.Last().OpenTime.AddMinutes(1);

            await Task.Delay(200);
        }

        logger.LogInformation("Получено {Count} свечей", allKlines.Count);
        return allKlines;
    }

    private static async Task RunBacktest(BinanceRestClient binanceClient, TelegramBotClient telegramBot, string text, TradingParams parameters = null)
    {
        parameters ??= new TradingParams(
            config.FastMAPeriod,
            config.SlowMAPeriod,
            config.RSIPeriod,
            config.OverboughtLevel,
            config.OversoldLevel);

        logger.LogInformation("Запуск бэктеста с {StartDate} по {EndDate}",
            config.BacktestStartDate, config.BacktestEndDate);

        var allKlines = await GetAllHistoricalData(binanceClient);
        if (allKlines == null || !allKlines.Any()) return;

        decimal balance = config.InitialBalance;
        decimal position = 0;
        decimal entryPrice = 0;
        var tradeHistory = new List<TradeRecord>();
        var equityCurve = new List<decimal>();

        for (int i = Math.Max(parameters.SlowMAPeriod, parameters.RSIPeriod); i < allKlines.Count; i++)
        {
            var currentKline = allKlines[i];
            var previousKlines = allKlines.Take(i).Select(k => (double)k.ClosePrice).ToArray();

            var fastMa = CalculateSma(previousKlines, parameters.FastMAPeriod);
            var slowMa = CalculateSma(previousKlines, parameters.SlowMAPeriod);
            var rsi = CalculateRsi(previousKlines, parameters.RSIPeriod);
            var currentPrice = (double)currentKline.ClosePrice;

            //if (i % 100 == 0)
            //{
            //    logger.LogInformation(
            //        "{Time} | Цена: {Price} | MA{fastPeriod}: {FastMA} | MA{slowPeriod}: {SlowMA} | RSI: {RSI} | Баланс: {Balance}",
            //        currentKline.OpenTime.ToString("yyyy-MM-dd HH:mm:ss"),
            //        currentPrice.ToString("F2"),
            //        parameters.FastMAPeriod,
            //        fastMa.ToString("F2"),
            //        parameters.SlowMAPeriod,
            //        slowMa.ToString("F2"),
            //        rsi.ToString("F2"),
            //        balance.ToString("F2"));
            //}

            bool isBullish = fastMa > slowMa && previousKlines[^2] <= slowMa && rsi < parameters.OverboughtLevel;
            bool isBearish = fastMa < slowMa && previousKlines[^2] >= slowMa && rsi > parameters.OversoldLevel;

            if (isBullish && position <= 0)
            {
                if (position < 0)
                {
                    var pnl = position * ((decimal)currentPrice - entryPrice);
                    balance += pnl;
                    tradeHistory.Add(new TradeRecord(
                        allKlines[i - 1].OpenTime,
                        "SELL",
                        Math.Abs(position),
                        entryPrice,
                        (decimal)currentPrice,
                        pnl));
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
                    0));
            }
            else if (isBearish && position >= 0)
            {
                if (position > 0)
                {
                    var pnl = position * ((decimal)currentPrice - entryPrice);
                    balance += pnl;
                    tradeHistory.Add(new TradeRecord(
                        allKlines[i - 1].OpenTime,
                        "SELL",
                        position,
                        entryPrice,
                        (decimal)currentPrice,
                        pnl));
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
                    0));
            }

            equityCurve.Add(balance + position * ((decimal)currentPrice - entryPrice));
        }

        if (position != 0)
        {
            var lastPrice = (double)allKlines.Last().ClosePrice;
            var pnl = position * ((decimal)lastPrice - entryPrice);
            balance += pnl;

            tradeHistory.Add(new TradeRecord(
                allKlines.Last().OpenTime,
                position > 0 ? "SELL" : "BUY",
                Math.Abs(position),
                entryPrice,
                (decimal)lastPrice,
                pnl));
        }

        logger.LogInformation("\n=== РЕЗУЛЬТАТЫ БЭКТЕСТА ===");
        logger.LogInformation("Использованные параметры: {Parameters}", parameters);
        logger.LogInformation("Начальный баланс: {InitialBalance}", config.InitialBalance);
        logger.LogInformation("Конечный баланс: {FinalBalance}", balance);
        logger.LogInformation("Прибыль: {PnL} ({Percentage}%)",
            (balance - config.InitialBalance).ToString("F2"),
            ((balance / config.InitialBalance - 1) * 100).ToString("F2"));

        int totalTrades = tradeHistory.Count(t => t.IsClosed);
        int profitableTrades = tradeHistory.Count(t => t.IsClosed && t.PnL > 0);

        logger.LogInformation("Сделок: {TradesCount}", totalTrades);
        logger.LogInformation("Прибыльных: {ProfitableTrades} ({Percentage}%)",
            profitableTrades,
            (totalTrades > 0 ? (double)profitableTrades / totalTrades * 100 : 0).ToString("F2"));

        if (tradeHistory.Any(t => t.IsClosed))
        {
            var maxDrawdown = CalculateMaxDrawdown(equityCurve);
            logger.LogInformation("Максимальная просадка: {MaxDrawdown}%", maxDrawdown.ToString("F2"));
        }

        await telegramBot.SendMessage(
            chatId: config.TelegramChatId,
            text: $"📊 Результаты бэктеста {text}: {config.Symbol}\n" +
                  $"Период: {config.BacktestStartDate:dd.MM.yyyy} - {config.BacktestEndDate:dd.MM.yyyy}\n" +
                  $"Параметры: {parameters}\n" +
                  $"Баланс: {config.InitialBalance:F2} → {balance:F2}\n" +
                  $"Прибыль: {(balance - config.InitialBalance):F2} ({(balance / config.InitialBalance - 1) * 100:F2}%)\n" +
                  $"Сделок: {totalTrades} | Прибыльных: {profitableTrades}");
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
        var klinesResult = await binanceClient.SpotApi.ExchangeData.GetKlinesAsync(
            config.Symbol,
            KlineInterval.OneHour,
            limit: Math.Max(config.SlowMAPeriod, config.RSIPeriod) + 50);

        if (!klinesResult.Success)
        {
            logger.LogError("Ошибка получения свечей: {Error}", klinesResult.Error);
            return;
        }

        var closes = klinesResult.Data.Select(k => (double)k.ClosePrice).ToArray();

        var fastMa = CalculateSma(closes, config.FastMAPeriod);
        var slowMa = CalculateSma(closes, config.SlowMAPeriod);
        var rsi = CalculateRsi(closes, config.RSIPeriod);

        var ticker = await binanceClient.SpotApi.ExchangeData.GetPriceAsync(config.Symbol);
        if (!ticker.Success)
        {
            logger.LogError("Ошибка получения цены: {Error}", ticker.Error);
            return;
        }
        var currentPrice = (double)ticker.Data.Price;

        logger.LogInformation(
            "{Time} | Цена: {Price} | MA{fastPeriod}: {FastMA} | MA{slowPeriod}: {SlowMA} | RSI: {RSI}",
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            currentPrice.ToString("F2"),
            config.FastMAPeriod,
            fastMa.ToString("F2"),
            config.SlowMAPeriod,
            slowMa.ToString("F2"),
            rsi.ToString("F2"));

        bool isBullish = fastMa > slowMa && closes[^2] <= slowMa && rsi < config.OverboughtLevel;
        bool isBearish = fastMa < slowMa && closes[^2] >= slowMa && rsi > config.OversoldLevel;

        if (isBullish)
        {
            await ExecuteTradeAsync(binanceClient, telegramBot, OrderSide.Buy, (decimal)currentPrice);
        }
        else if (isBearish)
        {
            await ExecuteTradeAsync(binanceClient, telegramBot, OrderSide.Sell, (decimal)currentPrice);
        }
    }

    private static async Task ExecuteTradeAsync(BinanceRestClient binanceClient, TelegramBotClient telegramBot, OrderSide side, decimal currentPrice)
    {
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
        }
        else
        {
            logger.LogError("Ошибка ордера: {Error}", order.Error);
            await telegramBot.SendMessage(
                chatId: config.TelegramChatId,
                text: $"❌ Ошибка: {order.Error}");
        }
    }

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
}