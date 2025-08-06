using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using CryptoExchange.Net.Authentication;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

// Конфигурация
var config = new
{
    ApiKey = "YOUR_BINANCE_API_KEY",
    ApiSecret = "YOUR_BINANCE_API_SECRET",
    TelegramToken = "6299377057:AAHaNlY93hdrdQVanTPgmMibgQt41UDidRA",
    TelegramChatId = "1314937104",
    Symbol = "BTCUSDT",
    RiskPerTrade = 0.02m,
    FastMAPeriod = 9,
    SlowMAPeriod = 21,
    RSIPeriod = 14,
    OverboughtLevel = 70.0,
    OversoldLevel = 30.0,
    CheckIntervalMinutes = 1,
    
    // Новые параметры для бэктестинга
    BacktestMode = true, // Включить/выключить бэктестинг
    BacktestStartDate = new DateTime(2023, 1, 1),
    BacktestEndDate = DateTime.Now,
    BacktestInterval = KlineInterval.OneHour,
    InitialBalance = 1000m // Начальный баланс для бэктестинга
};

// Настройка логгера
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger("CryptoBot");

// Инициализация клиентов
var binanceClient = new BinanceRestClient(options =>
{
    options.ApiCredentials = new ApiCredentials(config.ApiKey, config.ApiSecret);
});

var telegramBot = new TelegramBotClient(config.TelegramToken);

if (config.BacktestMode)
{
    await RunBacktest();
}
else
{
    logger.LogInformation("Бот запущен в реальном режиме. Мониторинг рынка...");
    await RunLiveTrading();
}

async Task RunLiveTrading()
{
    // Основной цикл для реальной торговли
    while (true)
    {
        try
        {
            await CheckMarketAndTradeAsync();
            await Task.Delay(TimeSpan.FromMinutes(config.CheckIntervalMinutes));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ошибка в основном цикле");
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }
}

async Task RunBacktest()
{
    logger.LogInformation("Запуск бэктеста с {StartDate} по {EndDate}", 
        config.BacktestStartDate, config.BacktestEndDate);
    
    // Получаем все исторические данные за указанный период
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
            logger.LogError("Ошибка получения исторических данных: {Error}", klinesResult.Error);
            return;
        }
        
        if (!klinesResult.Data.Any()) break;
        
        allKlines.AddRange(klinesResult.Data);
        currentStartTime = klinesResult.Data.Last().OpenTime.AddMinutes(1);
        
        // Чтобы не превысить лимиты API
        await Task.Delay(200);
    }
    
    if (!allKlines.Any())
    {
        logger.LogError("Не удалось получить исторические данные для бэктеста");
        return;
    }
    
    logger.LogInformation("Получено {Count} свечей для бэктеста", allKlines.Count);
    
    // Переменные для отслеживания состояния бэктеста
    decimal balance = config.InitialBalance;
    decimal position = 0;
    decimal entryPrice = 0;
    var tradeHistory = new List<TradeRecord>();
    var equityCurve = new List<decimal>();
    
    // Проходим по всем свечам и симулируем торговлю
    for (int i = Math.Max(config.SlowMAPeriod, config.RSIPeriod); i < allKlines.Count; i++)
    {
        var currentKline = allKlines[i];
        var previousKlines = allKlines.Take(i).Select(k => (double)k.ClosePrice).ToArray();
        
        // Рассчитываем индикаторы
        var fastMa = CalculateSma(previousKlines, config.FastMAPeriod);
        var slowMa = CalculateSma(previousKlines, config.SlowMAPeriod);
        var rsi = CalculateRsi(previousKlines, config.RSIPeriod);
        var currentPrice = (double)currentKline.ClosePrice;
        
        // Логируем информацию о текущем состоянии
        if (i % 100 == 0) // Логируем каждые 100 свечей для наглядности
        {
            logger.LogInformation(
                "{Time} | Цена: {Price} | MA{fastPeriod}: {FastMA} | MA{slowPeriod}: {SlowMA} | RSI: {RSI} | Баланс: {Balance}",
                currentKline.OpenTime.ToString("yyyy-MM-dd HH:mm:ss"),
                currentPrice.ToString("F2"),
                config.FastMAPeriod,
                fastMa.ToString("F2"),
                config.SlowMAPeriod,
                slowMa.ToString("F2"),
                rsi.ToString("F2"),
                balance.ToString("F2"));
        }
        
        // Условия для входа
        bool isBullish = fastMa > slowMa && previousKlines[^2] <= slowMa && rsi < config.OverboughtLevel;
        bool isBearish = fastMa < slowMa && previousKlines[^2] >= slowMa && rsi > config.OversoldLevel;
        
        // Симулируем торговлю
        if (isBullish && position <= 0)
        {
            // Закрываем короткую позицию, если есть
            if (position < 0)
            {
                var pnl = position * ((decimal)currentPrice - entryPrice);
                balance += pnl;
                tradeHistory.Add(new TradeRecord(
                    allKlines[i-1].OpenTime,
                    "SELL",
                    Math.Abs(position),
                    entryPrice,
                    (decimal)currentPrice,
                    pnl));
                
                logger.LogInformation("Закрытие короткой позиции. PnL: {PnL}", pnl.ToString("F2"));
            }
            
            // Открываем длинную позицию
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
            
            logger.LogInformation("Открытие длинной позиции: {Quantity} по {Price}", 
                quantity.ToString("F6"), entryPrice.ToString("F2"));
        }
        else if (isBearish && position >= 0)
        {
            // Закрываем длинную позицию, если есть
            if (position > 0)
            {
                var pnl = position * ((decimal)currentPrice - entryPrice);
                balance += pnl;
                tradeHistory.Add(new TradeRecord(
                    allKlines[i-1].OpenTime,
                    "SELL",
                    position,
                    entryPrice,
                    (decimal)currentPrice,
                    pnl));
                
                logger.LogInformation("Закрытие длинной позиции. PnL: {PnL}", pnl.ToString("F2"));
            }
            
            // Открываем короткую позицию
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
            
            logger.LogInformation("Открытие короткой позиции: {Quantity} по {Price}", 
                quantity.ToString("F6"), entryPrice.ToString("F2"));
        }
        
        equityCurve.Add(balance + position * ((decimal)currentPrice - entryPrice));
    }
    
    // Закрываем последнюю позицию, если она есть
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
        
        position = 0;
    }
    
    // Выводим результаты бэктеста
    logger.LogInformation("\n=== РЕЗУЛЬТАТЫ БЭКТЕСТА ===");
    logger.LogInformation("Начальный баланс: {InitialBalance}", config.InitialBalance);
    logger.LogInformation("Конечный баланс: {FinalBalance}", balance);
    logger.LogInformation("Прибыль/убыток: {PnL} ({Percentage}%)", 
        (balance - config.InitialBalance).ToString("F2"),
        ((balance / config.InitialBalance - 1) * 100).ToString("F2"));
    
    logger.LogInformation("Всего сделок: {TradesCount}", tradeHistory.Count(t => t.IsClosed));
    var profitableTrades = tradeHistory.Where(t => t.IsClosed && t.PnL > 0).Count();
    logger.LogInformation("Прибыльных сделок: {ProfitableTrades} ({Percentage}%)",
        profitableTrades,
        (tradeHistory.Count(t => t.IsClosed) > 0 ? 
            (double)profitableTrades / tradeHistory.Count(t => t.IsClosed) * 100 : 0).ToString("F2"));
    
    var avgProfit = tradeHistory.Where(t => t.IsClosed && t.PnL > 0).Average(t => t.PnL);
    var avgLoss = tradeHistory.Where(t => t.IsClosed && t.PnL < 0).Average(t => t.PnL);
    logger.LogInformation("Средняя прибыль: {AvgProfit} | Средний убыток: {AvgLoss}", 
        avgProfit.ToString("F2"), avgLoss.ToString("F2"));
    
    if (tradeHistory.Any(t => t.IsClosed))
    {
        var maxDrawdown = CalculateMaxDrawdown(equityCurve);
        logger.LogInformation("Максимальная просадка: {MaxDrawdown}%", maxDrawdown.ToString("F2"));
    }

    // Отправляем основные результаты в Telegram
    await telegramBot.SendMessage(
        chatId: config.TelegramChatId,
        text: $"📊 Результаты бэктеста {config.Symbol}\n" +
              $"Период: {config.BacktestStartDate:yyyy-MM-dd} - {config.BacktestEndDate:yyyy-MM-dd}\n" +
              $"Начальный баланс: {config.InitialBalance:F2}\n" +
              $"Конечный баланс: {balance:F2}\n" +
              $"Прибыль: {(balance - config.InitialBalance):F2} ({(balance / config.InitialBalance - 1) * 100:F2}%)\n" +
              $"Сделок: {tradeHistory.Count(t => t.IsClosed)}\n" +
              $"Прибыльных: {profitableTrades} ({(tradeHistory.Count(t => t.IsClosed) > 0 ? ((double)profitableTrades / tradeHistory.Count(t => t.IsClosed) * 100).ToString("F2") : "0.00")}%)");
}

async Task CheckMarketAndTradeAsync()
{
    // Получаем свечи
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

    // Рассчитываем индикаторы
    var fastMa = CalculateSma(closes, config.FastMAPeriod);
    var slowMa = CalculateSma(closes, config.SlowMAPeriod);
    var rsi = CalculateRsi(closes, config.RSIPeriod);

    // Текущая цена
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

    // Условия для входа
    bool isBullish = fastMa > slowMa && closes[^2] <= slowMa && rsi < config.OverboughtLevel;
    bool isBearish = fastMa < slowMa && closes[^2] >= slowMa && rsi > config.OversoldLevel;

    if (isBullish)
    {
        await ExecuteTradeAsync(OrderSide.Buy, (decimal)currentPrice);
    }
    else if (isBearish)
    {
        await ExecuteTradeAsync(OrderSide.Sell, (decimal)currentPrice);
    }
}

async Task ExecuteTradeAsync(OrderSide side, decimal currentPrice)
{
    // Получаем баланс
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

    // Рассчитываем размер позиции
    decimal quantity = (usdtBalance.Value * config.RiskPerTrade) / currentPrice;
    quantity = Math.Round(quantity, 6); // Округляем до 6 знаков

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

static double CalculateSma(double[] closes, int period)
{
    if (closes.Length < period) return 0;
    return closes.TakeLast(period).Average();
}

static double CalculateRsi(double[] closes, int period)
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

static decimal CalculateMaxDrawdown(List<decimal> equityCurve)
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

record TradeRecord(DateTime Timestamp, string Type, decimal Quantity, decimal EntryPrice, decimal ExitPrice, decimal PnL)
{
    public bool IsClosed => ExitPrice != 0;
}