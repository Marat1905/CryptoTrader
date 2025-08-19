using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Objects.Models.Spot;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Logging;
using Skender.Stock.Indicators;
using System.Globalization;
using Telegram.Bot;

public class BacktestResult
{
    public decimal TotalProfitPercent { get; set; }
    public decimal TotalProfit { get; set; }
    public decimal Fees { get; set; }
    public int TotalTrades { get; set; }
    public int WinTrades { get; set; }
    public decimal WinRate => TotalTrades > 0 ? (decimal)WinTrades / TotalTrades * 100 : 0;
    public decimal MaxDrawdown { get; set; }
    public decimal SharpeRatio { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal RecoveryFactor { get; set; }
    public decimal MaxDrawdownValue { get; set; }
}

public class TradeRecord
{
    public DateTime EntryDate { get; set; }
    public decimal EntryPrice { get; set; }
    public DateTime ExitDate { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Profit { get; set; }
    public bool IsWin { get; set; }
    public decimal VolatilityIndex { get; set; }
    public string ExitReason { get; set; }
    public decimal PositionSize { get; set; }
}

public class TradingBot
{
    private readonly string _symbol;
    private readonly int _atrPeriod = 14; // Уменьшенный период для 15-минутного ТФ
    private decimal _baseRiskPercent = 0.003m;
    private readonly int _minBarsBetweenTrades = 12;
    private readonly decimal _volatilityThreshold = 0.2m; // Повышенный порог волатильности
    private readonly decimal _minPositionSize = 0.0001m;
    private readonly ILogger _logger;
    private readonly ITelegramBotClient _botClient;
    private readonly string _chatId;
    private readonly bool _enableTradeNotifications;
    private readonly decimal _commissionRate = 0.001m;
    private int _lastTradeIndex = -1000;
    private const int MIN_BARS_BEFORE_EXIT = 8;

    private readonly int _ichimokuTenkan = 9;  // Оптимизировано для 15-минутного ТФ
    private readonly int _ichimokuKijun = 26;  // Оптимизировано для 15-минутного ТФ
    private readonly int _ichimokuSenkou = 52;
    private readonly int _adxPeriod = 14;
    private readonly int _rsiPeriod = 14;
    private readonly int _supertrendPeriod = 10;  // Уменьшенный период
    private readonly double _supertrendMultiplier = 3.0;

    public TradingBot(
        string symbol,
        ILogger logger,
        ITelegramBotClient botClient,
        string chatId,
        bool enableTradeNotifications)
    {
        _symbol = symbol;
        _logger = logger;
        _botClient = botClient;
        _chatId = chatId;
        _enableTradeNotifications = enableTradeNotifications;
    }

    public BacktestResult Backtest(List<IBinanceKline> historicalData)
    {
        var quotes = historicalData.Select(k => new Quote
        {
            Date = k.OpenTime,
            Open = k.OpenPrice,
            High = k.HighPrice,
            Low = k.LowPrice,
            Close = k.ClosePrice,
            Volume = k.Volume
        }).ToList();

        // Обновленные расчеты индикаторов
        var ichimoku = quotes.GetIchimoku(_ichimokuTenkan, _ichimokuKijun, _ichimokuSenkou).ToList();
        var adx = quotes.GetAdx(_adxPeriod).ToList();
        var obv = quotes.GetObv().ToList();
        var rsi = quotes.GetRsi(_rsiPeriod).ToList();
        var atr = quotes.GetAtr(_atrPeriod).ToList();
        var supertrend = quotes.GetSuperTrend(_supertrendPeriod, _supertrendMultiplier).ToList();

        // Результаты бэктеста
        decimal initialCapital = 1000m;
        decimal capital = initialCapital;
        decimal position = 0;
        int winCount = 0;
        int tradeCount = 0;
        decimal entryPrice = 0;
        decimal highestPrice = 0;
        decimal trailStopLevel = 0;
        decimal takeProfitLevel = 0;
        decimal maxPortfolio = initialCapital;
        decimal maxDrawdown = 0;
        decimal maxDrawdownValue = 0;
        decimal totalFees = 0m;
        bool tradingHalted = false;
        var trades = new List<TradeRecord>();
        var dailyReturns = new List<decimal>();
        decimal entryAtr = 0;
        decimal currentTradeAtrMultiplier = 3.5m;

        int startIndex = new[] {
            _ichimokuSenkou * 2,
            _adxPeriod,
            _rsiPeriod,
            _atrPeriod,
            _supertrendPeriod
        }.Max();

        for (int i = startIndex; i < quotes.Count; i++)
        {
            var quote = quotes[i];
            decimal currentPortfolio = capital + (position * quote.Close);

            if (currentPortfolio > maxPortfolio)
                maxPortfolio = currentPortfolio;

            decimal drawdown = (maxPortfolio - currentPortfolio) / maxPortfolio * 100;
            decimal drawdownValue = maxPortfolio - currentPortfolio;

            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
                maxDrawdownValue = drawdownValue;
            }

            // Динамическое управление риском
            _baseRiskPercent = drawdown >= 10m ? 0.001m :
                              drawdown >= 5m ? 0.002m : 0.003m;

            // Обновленные проверки null
            if (ichimoku[i].KijunSen == null || ichimoku[i].TenkanSen == null ||
                ichimoku[i].SenkouSpanA == null || ichimoku[i].SenkouSpanB == null ||
                adx[i].Adx == null || adx[i].Pdi == null || adx[i].Mdi == null ||
                obv[i].Obv == null || rsi[i].Rsi == null ||
                atr[i].Atr == null || supertrend[i].UpperBand == null) // Исправлено на LowerBand
                continue;

            decimal currentKijun = (decimal)ichimoku[i].KijunSen;
            decimal currentTenkan = (decimal)ichimoku[i].TenkanSen;
            decimal currentSenkouA = (decimal)ichimoku[i].SenkouSpanA;
            decimal currentSenkouB = (decimal)ichimoku[i].SenkouSpanB;
            decimal currentAdx = (decimal)adx[i].Adx;
            decimal currentPositiveDI = (decimal)adx[i].Pdi;
            decimal currentNegativeDI = (decimal)adx[i].Mdi;
            decimal currentObv = (decimal)obv[i].Obv;
            decimal currentRsi = (decimal)rsi[i].Rsi;
            decimal currentAtr = (decimal)atr[i].Atr;
            decimal currentSupertrend = (decimal)supertrend[i].UpperBand; // Исправлено для long позиций

            // Замена Chaikin Volatility на ATR-based
            decimal volatilityIndex = (currentAtr / quote.Close) * 100;

            // Круглосуточная торговля
            bool isActiveSession = true;

            // Ослабленное условие облака Ишимоку
            bool kumoCloudBullish = quote.Close > Math.Min(currentSenkouA, currentSenkouB);
            bool strongTrend = currentAdx > 15; // Сниженный порог ADX

            decimal dynamicRiskPercent = volatilityIndex > 1.5m ? _baseRiskPercent * 0.7m : _baseRiskPercent;
            int dynamicMinBars = (int)(_minBarsBetweenTrades * Math.Max(0.5m, 2.0m - volatilityIndex / 10));

            bool ichimokuBullish = currentTenkan > currentKijun;
            bool volumeOk = currentRsi > 35; // Упрощенное условие объема
            bool timeBetweenTrades = (i - _lastTradeIndex) >= dynamicMinBars;

            decimal bodySize = Math.Abs(quote.Open - quote.Close);
            decimal totalRange = quote.High - quote.Low;
            bool isGoodCandle = bodySize > totalRange * 0.2m;

            // Расширенное логирование условий
            if (i % 50 == 0)
            {
                _logger.LogDebug(
                    $"[{quote.Date}] Условия: " +
                    $"Сессия: {isActiveSession} | Облако: {kumoCloudBullish} | ADX: {currentAdx:F1}>20 | " +
                    $"Ишимоку: {ichimokuBullish} | RSI: {currentRsi:F1}>40 | " +
                    $"Волатильность: {volatilityIndex:F2}%>0.3 | " +
                    $"Дистанция: {i - _lastTradeIndex}>={dynamicMinBars}");
            }

            if (!tradingHalted && position == 0 && isActiveSession &&
                kumoCloudBullish && strongTrend && timeBetweenTrades &&
                ichimokuBullish && volumeOk &&
                volatilityIndex > _volatilityThreshold &&
                isGoodCandle)
            {
                entryPrice = quote.Close * 1.0015m;
                highestPrice = entryPrice;

                decimal takeProfitMultiplier = volatilityIndex > 1.5m ? 2.5m : 3.0m; // Увеличен множитель
                decimal stopDistance = currentAtr * 2.0m;
                trailStopLevel = entryPrice - stopDistance;
                takeProfitLevel = entryPrice + stopDistance * takeProfitMultiplier;

                decimal riskMultiplier = 1m;
                if (capital < initialCapital * 0.95m) riskMultiplier = 0.7m;
                else if (maxDrawdown > 5m) riskMultiplier = 0.5m;

                decimal tradeRiskPercent = dynamicRiskPercent * riskMultiplier;
                decimal riskAmount = capital * tradeRiskPercent;
                decimal positionSize = riskAmount / (entryPrice - trailStopLevel);

                decimal maxPosition = capital / entryPrice;
                position = Math.Min(positionSize, maxPosition);

                if (position < _minPositionSize)
                {
                    _logger.LogWarning($"Размер позиции {position:F6} слишком мал. Сделка пропущена.");
                    position = 0;
                    continue;
                }

                // Сохраняем ATR и множитель для текущей сделки
                entryAtr = currentAtr;
                currentTradeAtrMultiplier = volatilityIndex > 1.5m ? 3.0m : 3.5m;

                decimal tradeValue = position * entryPrice;
                decimal tradeFee = tradeValue * _commissionRate;
                capital -= tradeValue + tradeFee;
                totalFees += tradeFee;

                tradeCount++;
                _lastTradeIndex = i;

                _logger.LogInformation($"ПОКУПКА {_symbol} по {entryPrice:F4} | Размер: {position:F6} | Риск: {tradeRiskPercent * 100:F2}% | ATR: {currentAtr:F4}");
            }

            if (position > 0)
            {
                // Защита по максимальному падению на основе ATR
                decimal maxAllowedDrop = entryPrice - (currentTradeAtrMultiplier * entryAtr);
                if (quote.Low <= maxAllowedDrop)
                {
                    decimal exitPrice = Math.Max(quote.Open, maxAllowedDrop);
                    decimal tradeValue = position * exitPrice;
                    decimal tradeFee = tradeValue * _commissionRate;
                    capital += tradeValue - tradeFee;
                    totalFees += tradeFee;

                    _logger.LogWarning($"ЭКСТРЕННАЯ ПРОДАЖА {_symbol} по {exitPrice:F4} | Причина: резкое падение (ATR защита)");

                    trades.Add(new TradeRecord
                    {
                        EntryDate = quotes[_lastTradeIndex].Date,
                        EntryPrice = entryPrice,
                        ExitDate = quote.Date,
                        ExitPrice = exitPrice,
                        Profit = tradeValue - (position * entryPrice) - tradeFee,
                        IsWin = false,
                        VolatilityIndex = volatilityIndex,
                        ExitReason = "ATR_DRAWDOWN",
                        PositionSize = position
                    });

                    position = 0;
                    continue;
                }

                // Обновляем высшую цену и трейлинг-стоп
                if (quote.High > highestPrice)
                {
                    highestPrice = quote.High;
                    // Динамическое обновление стопа
                    decimal newStopLevel = Math.Max(trailStopLevel, highestPrice - currentAtr * 1.5m);
                    if (newStopLevel > trailStopLevel)
                    {
                        trailStopLevel = newStopLevel;
                        _logger.LogDebug($"Обновлен трейлинг-стоп: {trailStopLevel:F2}");
                    }
                }

                // Обновление стопа по супертренду
                decimal newSupertrendStop = Math.Max(trailStopLevel, currentSupertrend);
                if (newSupertrendStop > trailStopLevel)
                {
                    trailStopLevel = newSupertrendStop;
                    _logger.LogDebug($"Обновлен Supertrend стоп: {trailStopLevel:F2}");
                }

                // Частичное закрытие с увеличенным множителем
                if (quote.High >= entryPrice + currentAtr * 2.5m)
                {
                    decimal partialExitPercent = 0.5m;
                    decimal partialPosition = position * partialExitPercent;
                    decimal exitPrice = entryPrice + currentAtr * 2.5m;

                    decimal tradeValue = partialPosition * exitPrice;
                    decimal tradeFee = tradeValue * _commissionRate;
                    capital += tradeValue - tradeFee;
                    totalFees += tradeFee;

                    position -= partialPosition;

                    trades.Add(new TradeRecord
                    {
                        EntryDate = quotes[_lastTradeIndex].Date,
                        EntryPrice = entryPrice,
                        ExitDate = quote.Date,
                        ExitPrice = exitPrice,
                        Profit = tradeValue - (partialPosition * entryPrice) - tradeFee,
                        IsWin = true,
                        VolatilityIndex = volatilityIndex,
                        ExitReason = "PARTIAL_TP",
                        PositionSize = partialPosition
                    });

                    _logger.LogInformation($"ЧАСТИЧНАЯ ПРОДАЖА 50% {_symbol} по {exitPrice:F4}");
                }

                // Улучшенные условия выхода
                bool takeProfitHit = quote.High >= takeProfitLevel;
                bool stopHit = quote.Low <= trailStopLevel && quote.Close < trailStopLevel; // Фильтр ложных пробоев
                bool trendWeak = currentAdx < 18;
                bool kumoExit = quote.Close < Math.Min(currentSenkouA, currentSenkouB);

                bool isEarlyExit = (i - _lastTradeIndex) < MIN_BARS_BEFORE_EXIT;
                if (isEarlyExit && (trendWeak || kumoExit))
                {
                    _logger.LogDebug($"Пропуск раннего выхода по индикатору. Баров с входа: {i - _lastTradeIndex}");
                    trendWeak = false;
                    kumoExit = false;
                }

                if (takeProfitHit || stopHit || trendWeak || kumoExit)
                {
                    decimal exitPrice;
                    string reason;

                    if (takeProfitHit)
                    {
                        exitPrice = takeProfitLevel;
                        reason = "ТЕЙК-ПРОФИТ";
                    }
                    else if (stopHit)
                    {
                        exitPrice = trailStopLevel;
                        reason = "СТОП-ЛОСС";
                    }
                    else if (trendWeak)
                    {
                        exitPrice = quote.Close;
                        reason = "СЛАБЫЙ ТРЕНД";
                    }
                    else
                    {
                        exitPrice = quote.Close;
                        reason = "ВЫХОД ИЗ ОБЛАКА";
                    }

                    decimal tradeValue = position * exitPrice;
                    decimal tradeFee = tradeValue * _commissionRate;
                    capital += tradeValue - tradeFee;
                    totalFees += tradeFee;

                    bool isWin = exitPrice > entryPrice;
                    if (isWin) winCount++;

                    trades.Add(new TradeRecord
                    {
                        EntryDate = quotes[_lastTradeIndex].Date,
                        EntryPrice = entryPrice,
                        ExitDate = quote.Date,
                        ExitPrice = exitPrice,
                        Profit = tradeValue - (position * entryPrice) - tradeFee,
                        IsWin = isWin,
                        VolatilityIndex = volatilityIndex,
                        ExitReason = reason,
                        PositionSize = position
                    });

                    decimal profitPercent = (exitPrice - entryPrice) / entryPrice * 100;
                    _logger.LogInformation(
                        $"ПРОДАЖА {_symbol} по {exitPrice:F4} | " +
                        $"Причина: {reason} | " +
                        $"Прибыль: {profitPercent:F2}% | " +
                        $"Капитал: {capital:F2}");

                    position = 0;
                }
            }
        }

        if (position > 0)
        {
            decimal exitPrice = quotes.Last().Close;
            decimal tradeValue = position * exitPrice;
            decimal tradeFee = tradeValue * _commissionRate;
            capital += tradeValue - tradeFee;
            totalFees += tradeFee;

            bool isWin = exitPrice > entryPrice;
            if (isWin) winCount++;

            trades.Add(new TradeRecord
            {
                EntryDate = quotes[_lastTradeIndex].Date,
                EntryPrice = entryPrice,
                ExitDate = quotes.Last().Date,
                ExitPrice = exitPrice,
                Profit = tradeValue - (position * entryPrice) - tradeFee,
                IsWin = isWin,
                VolatilityIndex = (decimal)atr.Last().Atr / quotes.Last().Close * 100,
                ExitReason = "FORCE_CLOSE",
                PositionSize = position
            });

            decimal profitPercent = (exitPrice - entryPrice) / entryPrice * 100;
            _logger.LogInformation(
                $"ФИНАЛЬНАЯ ПРОДАЖА {_symbol} по {exitPrice:F4} | " +
                $"Прибыль: {profitPercent:F2}% | " +
                $"Капитал: {capital:F2}");
        }

        decimal totalProfit = capital - initialCapital;
        decimal totalProfitPercent = totalProfit / initialCapital * 100;

        decimal profitFactor = 1m;
        decimal recoveryFactor = 1m;
        if (trades.Count > 0)
        {
            decimal totalWins = trades.Where(t => t.IsWin).Sum(t => t.Profit);
            decimal totalLosses = Math.Abs(trades.Where(t => !t.IsWin).Sum(t => t.Profit));
            profitFactor = totalLosses > 0 ? totalWins / totalLosses : totalWins > 0 ? 10m : 1m;
            recoveryFactor = maxDrawdownValue > 0 ? totalProfit / maxDrawdownValue : totalProfit > 0 ? 10m : 1m;

            var worstTrades = trades
                .Where(t => !t.IsWin)
                .OrderBy(t => t.Profit)
                .Take(5);

            _logger.LogInformation("Худшие сделки:");
            foreach (var trade in worstTrades)
            {
                _logger.LogInformation(
                    $"{trade.EntryDate} → {trade.ExitDate} | " +
                    $"Убыток: {trade.Profit:F2} | " +
                    $"Причина: {trade.ExitReason} | " +
                    $"Волатильность: {trade.VolatilityIndex:F2}% | " +
                    $"Размер: {trade.PositionSize:F6}");
            }

            var lossReasons = trades
                .Where(t => !t.IsWin)
                .GroupBy(t => t.ExitReason)
                .Select(g => new {
                    Reason = g.Key,
                    AvgLoss = g.Average(t => t.Profit),
                    Count = g.Count()
                })
                .OrderBy(x => x.AvgLoss);

            _logger.LogInformation("Анализ убытков по причинам:");
            foreach (var reason in lossReasons)
            {
                _logger.LogInformation(
                    $"{reason.Reason}: {reason.Count} сделок | Средний убыток: {reason.AvgLoss:F2}");
            }
        }

        return new BacktestResult
        {
            TotalProfit = totalProfit,
            TotalProfitPercent = totalProfitPercent,
            Fees = totalFees,
            TotalTrades = tradeCount,
            WinTrades = winCount,
            MaxDrawdown = maxDrawdown,
            ProfitFactor = profitFactor,
            RecoveryFactor = recoveryFactor,
            MaxDrawdownValue = maxDrawdownValue
        };
    }
}

public class BotConfig
{
    public string ApiKey { get; set; } = "YOUR_BINANCE_API_KEY";
    public string ApiSecret { get; set; } = "YOUR_BINANCE_API_SECRET";
    public string TelegramToken { get; set; } = "6299377057:AAHaNlY93hdrdQVanTPgmMibgQt41UDidRA";
    public string TelegramChatId { get; set; } = "1314937104";
    public string Symbol { get; set; } = "BTCUSDT";
    public DateTime BacktestStartDate { get; set; } = new DateTime(2023, 1, 1);
    public DateTime BacktestEndDate { get; set; } = DateTime.UtcNow;
    public KlineInterval BacktestInterval { get; set; } = KlineInterval.FifteenMinutes;
    public bool EnableTradeNotifications { get; set; } = true;
    public decimal InitialBalance { get; set; } = 1000m;
}

class Program
{
    private static ILogger logger;

    static async Task Main()
    {
        BotConfig config = new BotConfig();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddFile("logs/bot_{Date}.log");
        });

        logger = loggerFactory.CreateLogger("CryptoBot");

        ITelegramBotClient telegramBot = null;
        if (!string.IsNullOrEmpty(config.TelegramToken))
        {
            telegramBot = new TelegramBotClient(config.TelegramToken);
        }

        var binanceClient = new BinanceRestClient(options =>
        {
            options.ApiCredentials = new ApiCredentials(config.ApiKey, config.ApiSecret);
        });

        List<IBinanceKline> historicalData = await GetAllHistoricalDataAsync(
            binanceClient,
            config,
            logger);

        if (historicalData == null || historicalData.Count == 0)
        {
            logger.LogError("Исторические данные не загружены");
            return;
        }

        var bot = new TradingBot(
            config.Symbol,
            logger,
            telegramBot,
            config.TelegramChatId,
            config.EnableTradeNotifications);

        BacktestResult result = bot.Backtest(historicalData);

        var culture = CultureInfo.GetCultureInfo("ru-RU");
        string resultsMessage = $"""
            📊 Результаты бэктеста: {config.Symbol}
            Период: {config.BacktestStartDate:dd.MM.yyyy} - {config.BacktestEndDate:dd.MM.yyyy}
            Таймфрейм: {config.BacktestInterval}
            Баланс: {config.InitialBalance.ToString("N2", culture)} → {(config.InitialBalance + result.TotalProfit).ToString("N2", culture)}
            Прибыль: {result.TotalProfit.ToString("N2", culture)} ({result.TotalProfitPercent.ToString("N2", culture)}%)
            Комиссии: {result.Fees.ToString("N2", culture)}
            Сделок: {result.TotalTrades} | Прибыльных: {result.WinRate.ToString("N2", culture)}%
            Просадка: {result.MaxDrawdown.ToString("N2", culture)}% ({result.MaxDrawdownValue.ToString("N2", culture)})
            Profit Factor: {result.ProfitFactor.ToString("N2", culture)}
            Recovery Factor: {result.RecoveryFactor.ToString("N2", culture)}
            """;

        if (telegramBot != null)
        {
            try
            {
                await telegramBot.SendMessage(config.TelegramChatId, resultsMessage);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка отправки результатов в Telegram");
            }
        }

        Console.WriteLine("\n" + resultsMessage);
    }

    private static async Task<List<IBinanceKline>> GetAllHistoricalDataAsync(
        BinanceRestClient client,
        BotConfig config,
        ILogger logger)
    {
        var allKlines = new List<IBinanceKline>();
        var currentStartTime = config.BacktestStartDate;
        int maxLimit = 1000;

        try
        {
            logger.LogInformation("Загрузка исторических данных...");

            while (currentStartTime < config.BacktestEndDate)
            {
                var endTime = currentStartTime.AddDays(30);
                if (endTime > config.BacktestEndDate)
                    endTime = config.BacktestEndDate;

                var klinesResult = await client.SpotApi.ExchangeData.GetKlinesAsync(
                    config.Symbol,
                    config.BacktestInterval,
                    startTime: currentStartTime,
                    endTime: endTime,
                    limit: maxLimit);

                if (!klinesResult.Success)
                {
                    logger.LogError($"Ошибка: {klinesResult.Error}");
                    return null;
                }

                var klines = klinesResult.Data.ToList();
                if (klines.Count == 0)
                {
                    logger.LogWarning($"Нет данных для {currentStartTime:yyyy-MM-dd}");
                    currentStartTime = endTime.AddMilliseconds(1);
                    continue;
                }

                allKlines.AddRange(klines);
                currentStartTime = klines.Max(k => k.OpenTime).AddMilliseconds(1);

                logger.LogDebug($"Загружено {klines.Count} свечей | Текущая: {currentStartTime:yyyy-MM-dd HH:mm}");

                await Task.Delay(200);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка загрузки исторических данных");
            return null;
        }

        return allKlines
            .GroupBy(k => k.OpenTime)
            .Select(g => g.First())
            .OrderBy(k => k.OpenTime)
            .ToList();
    }
}