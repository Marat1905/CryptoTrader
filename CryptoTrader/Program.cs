using Binance.Net.Clients;
using Binance.Net.Enums;
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
    CheckIntervalMinutes = 1
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

logger.LogInformation("Бот запущен. Мониторинг рынка...");


// Основной цикл
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