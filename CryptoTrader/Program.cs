using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects;
using Binance.Net.Objects.Models.Spot;
using Binance.Net.Objects.Models.Spot.Socket;
using Binance.Net.Objects.Spot.SpotData;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TALib;
using Telegram.Bot;
using TicTacTec.TA.Library;

namespace AdvancedCryptoTradingBot
{
    /// <summary>
    /// Улучшенная версия крипто-трейдинг бота с исправлением всех выявленных проблем
    /// </summary>
    public class EnhancedCryptoTrader : IDisposable
    {
        #region Configuration
        private const string Symbol = "BTCUSDT";
        private readonly List<string> _secondarySymbols = new() { "ETHUSDT", "BNBUSDT" };
        private readonly List<KlineInterval> _timeFrames = new()
        {
            KlineInterval.FiveMinutes,
            KlineInterval.FifteenMinutes,
            KlineInterval.OneHour
        };

        // Конфигурируемые параметры
        private decimal _initialBalance = 10000m;
        private decimal _maxRiskPerTrade = 0.02m;
        private decimal _commissionRate = 0.001m;
        private decimal _maxAllowedDrawdown = 0.25m;
        private int _maxTradesPerDay = 20;
        private decimal _slippagePct = 0.1m;
        private int _emergencyStopLossPct = 15;
        private decimal _minConfidenceThreshold = 0.7m;
        private decimal _sharpeRatioThreshold = 1.0m;
        private int _minBacktestBars = 100;
        private int _circuitBreakerThreshold = 5;
        private int _maxConsecutiveLosses = 3;
        private decimal _minLiquidity = 10000m;
        private decimal _maxVolatility = 0.1m;

        private static readonly TimeSpan ApiRetryDelay = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(30);
        private const int HistoryBarsForVolatility = 100;
        private const int VolatilityLookbackPeriod = 14;
        private const decimal KellyFraction = 0.5m;
        private const int HeartbeatIntervalMinutes = 5;
        private const int LatencyMonitoringWindow = 100;
        private const decimal MaxAllowedLatencyMs = 300m;
        private const decimal VarConfidenceLevel = 0.95m;
        private const int BacktestLookbackDays = 365;
        private const int MaxOrderRetries = 3;
        private const decimal SandboxTestAmount = 100m; // Сумма для тестов в песочнице
        #endregion

        #region Components
        private readonly IBinanceRestClient _restClient;
        private readonly IBinanceSocketClient _socketClient;
        private readonly TelegramBotClient? _telegramBot;
        private readonly MLContext _mlContext = new(seed: 1);
        private ITransformer? _model;
        private readonly SqliteConnection _dbConnection;
        private readonly AsyncRetryPolicy _retryPolicy;
        private readonly ILogger _logger;
        private readonly bool _isSandboxMode;

        private readonly EnhancedRiskEngine _riskEngine;
        private readonly EnhancedExecutionEngine _executionEngine;
        private readonly MultiTimeFrameMarketDataProcessor _marketDataProcessor;
        private readonly PortfolioManager _portfolioManager;
        private readonly CorrelationAnalyzer _correlationAnalyzer;
        private readonly OnlineModelTrainer _onlineModelTrainer;
        private readonly Backtester _backtester;
        private readonly NewsMonitor _newsMonitor;
        #endregion

        #region State
        private volatile bool _isRunning;
        private DateTime _lastTradeTime;
        private decimal _currentBalance;
        private readonly ConcurrentDictionary<string, decimal> _volatilityCache = new();
        private readonly ConcurrentDictionary<string, decimal> _liquidityCache = new();
        private readonly ConcurrentDictionary<string, decimal> _currentPrices = new();
        private readonly ConcurrentDictionary<string, OrderBook> _orderBooks = new();
        private readonly List<OpenPosition> _openPositions = new();
        private readonly ConcurrentQueue<TradeRecord> _tradeHistory = new();
        private readonly object _tradingLock = new();
        private int _consecutiveLosses;
        private int _consecutiveErrors;
        private bool _circuitBreakerTriggered;
        private DateTime _lastHealthCheck;
        private MarketTrend _currentMarketTrend = MarketTrend.Neutral;
        #endregion

        public EnhancedCryptoTrader(bool isSandboxMode = true)
        {
            _isSandboxMode = isSandboxMode;
            InitializeLogger();

            // Политика повторных попыток с экспоненциальной задержкой
            _retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(
                    MaxOrderRetries,
                    attempt => TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)),
                    (ex, delay) => _logger.LogWarning($"Повторная попытка через {delay.TotalSeconds} сек: {ex.Message}"));

            LoadConfiguration();

            // Инициализация API клиентов
            var apiKey = Environment.GetEnvironmentVariable("BINANCE_API_KEY") ??
                throw new ArgumentNullException("BINANCE_API_KEY не установлен");
            var apiSecret = Environment.GetEnvironmentVariable("BINANCE_API_SECRET") ??
                throw new ArgumentNullException("BINANCE_API_SECRET не установлен");

            var clientOptions = new BinanceClientOptions
            {
                ApiCredentials = new ApiCredentials(apiKey, apiSecret),
                SpotApiOptions = new BinanceApiClientOptions
                {
                    RateLimitingBehaviour = RateLimitingBehaviour.Fail,
                    RateLimiters = new List<CryptoExchange.Net.RateLimiter.IRateLimiter>
                    {
                        new CryptoExchange.Net.RateLimiter.TokenBucketRateLimiter(10, 1, TimeSpan.FromSeconds(1))
                    }
                },
                RequestTimeout = TimeSpan.FromSeconds(10),
                SpotApiOptions = new BinanceApiClientOptions
                {
                    BaseAddress = _isSandboxMode ?
                        "https://testnet.binance.vision" :
                        "https://api.binance.com"
                }
            };

            var socketOptions = new BinanceSocketClientOptions
            {
                ApiCredentials = new ApiCredentials(apiKey, apiSecret),
                AutoReconnect = true,
                ReconnectInterval = TimeSpan.FromSeconds(5),
                SocketNoDataTimeout = TimeSpan.FromSeconds(30)
            };

            _restClient = new BinanceRestClient(clientOptions);
            _socketClient = new BinanceSocketClient(socketOptions);

            // Инициализация базы данных
            _dbConnection = new SqliteConnection("Data Source=trading.db");
            _dbConnection.Open();

            InitializeTelegram();
            InitializeComponents();
        }

        private void InitializeLogger()
        {
            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole().AddDebug();
                builder.AddFile("logs/bot_{Date}.log");
            });
            _logger = loggerFactory.CreateLogger<EnhancedCryptoTrader>();
        }

        private void LoadConfiguration()
        {
            try
            {
                using var cmd = _dbConnection.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Configuration (
                        InitialBalance REAL NOT NULL DEFAULT 10000,
                        MaxRiskPerTrade REAL NOT NULL DEFAULT 0.02,
                        CommissionRate REAL NOT NULL DEFAULT 0.001,
                        MaxAllowedDrawdown REAL NOT NULL DEFAULT 0.25,
                        MaxTradesPerDay INTEGER NOT NULL DEFAULT 20,
                        SlippagePct REAL NOT NULL DEFAULT 0.1,
                        EmergencyStopLossPct INTEGER NOT NULL DEFAULT 15,
                        MinConfidenceThreshold REAL NOT NULL DEFAULT 0.7,
                        SharpeRatioThreshold REAL NOT NULL DEFAULT 1.0,
                        MinBacktestBars INTEGER NOT NULL DEFAULT 100,
                        CircuitBreakerThreshold INTEGER NOT NULL DEFAULT 5,
                        MaxConsecutiveLosses INTEGER NOT NULL DEFAULT 3,
                        MinLiquidity REAL NOT NULL DEFAULT 10000,
                        MaxVolatility REAL NOT NULL DEFAULT 0.1,
                        UpdatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "INSERT OR IGNORE INTO Configuration DEFAULT VALUES";
                cmd.ExecuteNonQuery();

                cmd.CommandText = "SELECT * FROM Configuration LIMIT 1";
                using var reader = cmd.ExecuteReader();

                if (reader.Read())
                {
                    _initialBalance = reader.GetDecimal(0);
                    _maxRiskPerTrade = reader.GetDecimal(1);
                    _commissionRate = reader.GetDecimal(2);
                    _maxAllowedDrawdown = reader.GetDecimal(3);
                    _maxTradesPerDay = reader.GetInt32(4);
                    _slippagePct = reader.GetDecimal(5);
                    _emergencyStopLossPct = reader.GetInt32(6);
                    _minConfidenceThreshold = reader.GetDecimal(7);
                    _sharpeRatioThreshold = reader.GetDecimal(8);
                    _minBacktestBars = reader.GetInt32(9);
                    _circuitBreakerThreshold = reader.GetInt32(10);
                    _maxConsecutiveLosses = reader.GetInt32(11);
                    _minLiquidity = reader.GetDecimal(12);
                    _maxVolatility = reader.GetDecimal(13);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки конфигурации");
                throw;
            }
        }

        private void InitializeTelegram()
        {
            var telegramToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
            if (!string.IsNullOrEmpty(telegramToken))
            {
                try
                {
                    _telegramBot = new TelegramBotClient(telegramToken);
                    _telegramBot.TestApiAsync().Wait();
                    _logger.LogInformation("Telegram бот успешно инициализирован");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка инициализации Telegram бота");
                }
            }
        }

        private void InitializeComponents()
        {
            _riskEngine = new EnhancedRiskEngine(
                maxDrawdown: _maxAllowedDrawdown,
                maxRiskPerTrade: _maxRiskPerTrade,
                kellyFraction: KellyFraction,
                maxLeverage: 3m,
                varConfidenceLevel: VarConfidenceLevel,
                maxVolatility: _maxVolatility,
                minLiquidity: _minLiquidity,
                maxConsecutiveLosses: _maxConsecutiveLosses);

            _executionEngine = new EnhancedExecutionEngine(
                restClient: _restClient,
                socketClient: _socketClient,
                retryPolicy: _retryPolicy,
                logger: _logger,
                isSandbox: _isSandboxMode);

            _marketDataProcessor = new MultiTimeFrameMarketDataProcessor(_timeFrames);

            _portfolioManager = new PortfolioManager(
                initialBalance: _isSandboxMode ? SandboxTestAmount : _initialBalance,
                riskEngine: _riskEngine,
                logger: _logger);

            _correlationAnalyzer = new CorrelationAnalyzer(
                symbols: _secondarySymbols.Prepend(Symbol).ToList(),
                logger: _logger);

            _onlineModelTrainer = new OnlineModelTrainer(
                mlContext: _mlContext,
                lookbackWindow: 2000,
                logger: _logger);

            _backtester = new Backtester(
                _restClient,
                _marketDataProcessor,
                _logger,
                _timeFrames);

            _newsMonitor = new NewsMonitor(_logger);
        }

        public async Task Run(bool enableLiveTrading = false)
        {
            try
            {
                _isRunning = true;
                await InitializeDatabase();
                await LoadBotState();

                // Проверка подключения к API
                if (!await CheckExchangeConnectivity())
                {
                    await Notify("⚠️ Ошибка подключения к бирже. Бот остановлен.");
                    return;
                }

                // Запуск мониторинга новостей
                _newsMonitor.StartMonitoring();

                // Расширенный бэктест на нескольких таймфреймах
                var backtestResults = new List<BacktestResult>();
                foreach (var timeFrame in _timeFrames)
                {
                    var result = await RunExtendedBacktest(timeFrame);
                    backtestResults.Add(result);
                }

                // Проверка результатов бэктеста
                if (backtestResults.Any(r => !r.Success) ||
                    backtestResults.Average(r => r.SharpeRatio) < _sharpeRatioThreshold)
                {
                    await Notify($"⚠️ Бэктест не пройден. Средний Sharpe: {backtestResults.Average(r => r.SharpeRatio):F2} (< {_sharpeRatioThreshold})");
                    return;
                }

                if (enableLiveTrading)
                {
                    await ConnectWebSockets();
                    await Notify($"🚀 Торговый бот запущен в {(_isSandboxMode ? "песочнице" : "реальном режиме")}");
                    await MainTradingLoop();
                }
            }
            catch (Exception ex)
            {
                await HandleCriticalError(ex);
            }
            finally
            {
                await Cleanup();
            }
        }

        private async Task<bool> CheckExchangeConnectivity()
        {
            try
            {
                var pingResult = await _retryPolicy.ExecuteAsync(() =>
                    _restClient.SpotApi.ExchangeData.PingAsync());

                var serverTime = await _retryPolicy.ExecuteAsync(() =>
                    _restClient.SpotApi.ExchangeData.GetServerTimeAsync());

                var symbolData = await _retryPolicy.ExecuteAsync(() =>
                    _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(Symbol));

                return pingResult.Success && serverTime.Success && symbolData.Success;
            }
            catch
            {
                return false;
            }
        }

        private async Task<BacktestResult> RunExtendedBacktest(KlineInterval timeFrame)
        {
            var results = new List<BacktestResult>();

            // Основной бэктест
            var mainResult = await _backtester.RunBacktest(
                Symbol,
                timeFrame,
                BacktestLookbackDays);
            results.Add(mainResult);

            // Бэктест в условиях высокой волатильности
            var volatileResult = await _backtester.RunBacktest(
                Symbol,
                timeFrame,
                lookbackDays: 30,
                filter: "HighVolatility");
            results.Add(volatileResult);

            // Бэктест в условиях низкой ликвидности
            var lowLiqResult = await _backtester.RunBacktest(
                Symbol,
                timeFrame,
                lookbackDays: 30,
                filter: "LowLiquidity");
            results.Add(lowLiqResult);

            return new BacktestResult
            {
                Success = results.All(r => r.Success),
                SharpeRatio = results.Average(r => r.SharpeRatio),
                MaxDrawdown = results.Max(r => r.MaxDrawdown),
                WinRate = results.Average(r => r.WinRate),
                TotalReturn = results.Average(r => r.TotalReturn),
                TimeFrame = timeFrame.ToString()
            };
        }

        private async Task MainTradingLoop()
        {
            _logger.LogInformation("Главный торговый цикл запущен");

            // Таймер для периодических задач
            var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(10));

            while (_isRunning && await periodicTimer.WaitForNextTickAsync())
            {
                try
                {
                    if (_circuitBreakerTriggered)
                    {
                        await CheckCircuitBreakerConditions();
                        continue;
                    }

                    // Проверка состояния системы
                    if (!await CheckSystemHealth())
                    {
                        await Task.Delay(5000);
                        continue;
                    }

                    // Проверка новостного фона
                    if (_newsMonitor.IsHighImpactNewsPending())
                    {
                        await HandleNewsEvent();
                        continue;
                    }

                    // Получение рыночных данных
                    var marketData = await GetLatestMarketData();

                    // Анализ тренда
                    _currentMarketTrend = _marketDataProcessor.DetermineMarketTrend(marketData);

                    // Расчет метрик риска
                    var riskMetrics = await CalculateRiskMetrics(marketData);

                    // Генерация сигналов
                    var signals = await GenerateSignals(marketData);

                    // Фильтрация сигналов
                    signals = FilterSignals(signals, riskMetrics);

                    // Исполнение сделок
                    await ExecuteTrades(signals, riskMetrics);

                    // Обновление моделей ML
                    await _onlineModelTrainer.UpdateModels(marketData);

                    // Периодические задачи
                    await PerformPeriodicTasks();
                }
                catch (Exception ex)
                {
                    await HandleCriticalError(ex);
                }
            }
        }

        private List<TradingSignal> FilterSignals(List<TradingSignal> signals, RiskMetrics riskMetrics)
        {
            return signals.Where(s =>
                _liquidityCache.GetValueOrDefault(s.Symbol, 0) > _minLiquidity &&
                _volatilityCache.GetValueOrDefault(s.Symbol, 0) < _maxVolatility &&
                s.Confidence > _minConfidenceThreshold &&
                _riskEngine.IsTradeAllowed(s, riskMetrics, _currentMarketTrend) &&
                !_newsMonitor.IsSymbolAffected(s.Symbol)
            ).ToList();
        }

        private async Task HandleNewsEvent()
        {
            var affectedSymbols = _newsMonitor.GetAffectedSymbols();
            await Notify($"⚠️ Обнаружены важные новости по: {string.Join(", ", affectedSymbols)}");

            // Закрываем позиции по затронутым символам
            foreach (var position in _openPositions.Where(p => affectedSymbols.Contains(p.Symbol)))
            {
                await ClosePosition(position, "News event");
            }

            // Ждем завершения новостного события
            await Task.Delay(TimeSpan.FromMinutes(30));
        }

        private async Task PerformPeriodicTasks()
        {
            try
            {
                // Обновление баланса
                await _portfolioManager.UpdateBalanceFromExchange(_restClient);

                // Проверка и закрытие позиций по стоп-лоссу/тейк-профиту
                await CheckOpenPositions();

                // Сохранение состояния
                await UpdateBotState();

                // Очистка старых данных
                CleanupOldData();

                // Отправка отчета
                if (DateTime.UtcNow.Hour == 8 && DateTime.UtcNow.Minute < 10)
                {
                    await SendDailyReport();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при выполнении периодических задач");
            }
        }

        private async Task CheckOpenPositions()
        {
            foreach (var position in _openPositions.ToList())
            {
                var currentPrice = _currentPrices.GetValueOrDefault(position.Symbol);
                if (currentPrice == 0) continue;

                // Проверка стоп-лосса
                if ((position.Direction == TradeDirection.Long && currentPrice <= position.StopLoss) ||
                    (position.Direction == TradeDirection.Short && currentPrice >= position.StopLoss))
                {
                    await ClosePosition(position, "Stop loss triggered");
                    continue;
                }

                // Проверка тейк-профита
                if ((position.Direction == TradeDirection.Long && currentPrice >= position.TakeProfit) ||
                    (position.Direction == TradeDirection.Short && currentPrice <= position.TakeProfit))
                {
                    await ClosePosition(position, "Take profit triggered");
                    continue;
                }

                // Трейлинг-стоп
                UpdateTrailingStop(position, currentPrice);
            }
        }

        private async Task ClosePosition(OpenPosition position, string reason)
        {
            try
            {
                var orderResult = await _executionEngine.ClosePosition(position);
                if (orderResult.Success)
                {
                    var trade = new TradeRecord
                    {
                        Symbol = position.Symbol,
                        Side = position.Direction == TradeDirection.Long ? "SELL" : "BUY",
                        Quantity = position.Quantity,
                        EntryPrice = position.EntryPrice,
                        ExitPrice = orderResult.AveragePrice,
                        EntryTime = position.EntryTime,
                        ExitTime = DateTime.UtcNow,
                        Profit = (orderResult.AveragePrice - position.EntryPrice) * position.Quantity *
                                (position.Direction == TradeDirection.Long ? 1 : -1),
                        Commission = orderResult.Commission,
                        StopLoss = position.StopLoss,
                        TakeProfit = position.TakeProfit,
                        ExitReason = reason
                    };

                    _portfolioManager.RecordTrade(trade);
                    _tradeHistory.Enqueue(trade);
                    await SaveTrade(trade);

                    _openPositions.Remove(position);
                    await Notify($"ℹ️ Позиция закрыта: {position.Symbol}. Причина: {reason}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка закрытия позиции {position.Symbol}");
            }
        }

        private void UpdateTrailingStop(OpenPosition position, decimal currentPrice)
        {
            if (position.Direction == TradeDirection.Long)
            {
                var newStop = currentPrice - position.StopLossDistance * 1.5m;
                if (newStop > position.StopLoss)
                {
                    position.StopLoss = newStop;
                }
            }
            else
            {
                var newStop = currentPrice + position.StopLossDistance * 1.5m;
                if (newStop < position.StopLoss)
                {
                    position.StopLoss = newStop;
                }
            }
        }

        private async Task SendDailyReport()
        {
            try
            {
                var todayTrades = _tradeHistory
                    .Where(t => t.EntryTime.Date == DateTime.UtcNow.Date)
                    .ToList();

                if (!todayTrades.Any()) return;

                var profit = todayTrades.Sum(t => t.Profit ?? 0);
                var winRate = todayTrades.Count(t => t.Profit > 0) / (decimal)todayTrades.Count;
                var message = $"📊 Дневной отчет:\n" +
                               $"Сделок: {todayTrades.Count}\n" +
                               $"Прибыль: {profit:F2} USDT\n" +
                               $"Win Rate: {winRate:P1}\n" +
                               $"Баланс: {_currentBalance:F2} USDT";

                await Notify(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка формирования дневного отчета");
            }
        }

        private void CleanupOldData()
        {
            try
            {
                // Очистка старых данных из кэша
                var cutoffDate = DateTime.UtcNow.AddDays(-7);
                var oldTrades = _tradeHistory.Where(t => t.EntryTime < cutoffDate).ToList();
                foreach (var trade in oldTrades)
                {
                    _tradeHistory.TryDequeue(out _);
                }

                // Очистка БД
                using var cmd = _dbConnection.CreateCommand();
                cmd.CommandText = "DELETE FROM Trades WHERE EntryTime < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", cutoffDate);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка очистки старых данных");
            }
        }

        private async Task<List<MarketDataPoint>> GetLatestMarketData()
        {
            var allData = new List<MarketDataPoint>();

            foreach (var timeFrame in _timeFrames)
            {
                var klinesResult = await _retryPolicy.ExecuteAsync(() =>
                    _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                        Symbol,
                        timeFrame,
                        limit: 1000));

                if (!klinesResult.Success)
                {
                    _logger.LogWarning($"Не удалось получить данные для {timeFrame}: {klinesResult.Error}");
                    continue;
                }

                var timeFrameData = klinesResult.Data.Select(k => new MarketDataPoint
                {
                    Symbol = Symbol,
                    TimeFrame = timeFrame,
                    OpenTime = k.OpenTime,
                    Open = k.OpenPrice,
                    High = k.HighPrice,
                    Low = k.LowPrice,
                    Close = k.ClosePrice,
                    Volume = k.Volume,
                    IsClosed = true
                }).ToList();

                foreach (var data in timeFrameData)
                {
                    _marketDataProcessor.CalculateIndicators(data);
                }

                allData.AddRange(timeFrameData);
            }

            return allData;
        }

        private async Task<RiskMetrics> CalculateRiskMetrics(List<MarketDataPoint> marketData)
        {
            var volatility = _marketDataProcessor.CalculateVolatility(marketData, VolatilityLookbackPeriod);
            _volatilityCache[Symbol] = volatility;

            var liquidity = _orderBooks.TryGetValue(Symbol, out var book) ?
                book.CalculateLiquidity(_currentPrices[Symbol]) : 0m;
            _liquidityCache[Symbol] = liquidity;

            var portfolioRisk = _riskEngine.CalculatePortfolioRisk(
                _openPositions,
                _currentPrices,
                _volatilityCache);

            var cvar = _riskEngine.CalculateCVaR(
                _tradeHistory.ToList(),
                VarConfidenceLevel);

            return new RiskMetrics
            {
                Volatility = volatility,
                Liquidity = liquidity,
                PortfolioRisk = portfolioRisk,
                CVaR = cvar,
                CorrelationMatrix = _correlationAnalyzer.GetCorrelationMatrix(),
                PortfolioValue = _portfolioManager.CurrentBalance,
                OpenPositions = _openPositions,
                MarketTrend = _currentMarketTrend
            };
        }

        private async Task<List<TradingSignal>> GenerateSignals(List<MarketDataPoint> marketData)
        {
            var signals = new List<TradingSignal>();
            var latestData = marketData
                .GroupBy(d => d.TimeFrame)
                .Select(g => g.OrderByDescending(d => d.OpenTime).First())
                .ToList();

            foreach (var data in latestData)
            {
                // Генерация сигналов на основе TA
                var taSignal = _marketDataProcessor.GenerateTaSignal(data);

                // Генерация сигналов ML
                var mlPrediction = _onlineModelTrainer.Predict(data);

                // Генерация сигналов на основе корреляции
                var corrSignal = _correlationAnalyzer.GenerateCorrelationSignal(data.Symbol);

                // Консенсус сигналов
                if (taSignal.Direction == mlPrediction.Direction &&
                    (corrSignal == null || taSignal.Direction == corrSignal.Direction))
                {
                    var confidence = (taSignal.Confidence + mlPrediction.Confidence) / 2;
                    if (corrSignal != null) confidence = (confidence + corrSignal.Confidence) / 2;

                    signals.Add(new TradingSignal
                    {
                        Symbol = data.Symbol,
                        Direction = taSignal.Direction,
                        Confidence = confidence,
                        Timestamp = DateTime.UtcNow,
                        TimeFrame = data.TimeFrame,
                        Features = new Dictionary<string, object>
                        {
                            { "RSI", data.RSI },
                            { "MACD", data.MACD },
                            { "ATR", data.ATR },
                            { "Volume", data.Volume }
                        }
                    });
                }
            }

            return signals;
        }

        private async Task ExecuteTrades(List<TradingSignal> signals, RiskMetrics riskMetrics)
        {
            if (signals.Count == 0 || _openPositions.Count >= _maxTradesPerDay)
                return;

            foreach (var signal in signals)
            {
                try
                {
                    // Проверка ликвидности
                    var orderBook = await _retryPolicy.ExecuteAsync(() =>
                        _restClient.SpotApi.ExchangeData.GetOrderBookAsync(signal.Symbol, 10));

                    if (!orderBook.Success || orderBook.Data.Asks.Count == 0)
                    {
                        _logger.LogWarning($"Проверка ликвидности не пройдена для {signal.Symbol}");
                        continue;
                    }

                    // Расчет размера позиции
                    var positionSize = _portfolioManager.CalculatePositionSize(
                        signal,
                        _currentPrices[signal.Symbol],
                        riskMetrics);

                    if (positionSize <= 0)
                    {
                        _logger.LogInformation($"Нулевой размер позиции для {signal.Symbol}");
                        continue;
                    }

                    // Исполнение ордера
                    var orderResult = await _executionEngine.ExecuteOrder(new OrderRequest
                    {
                        Symbol = signal.Symbol,
                        Side = signal.Direction == TradeDirection.Long ?
                            OrderSide.Buy : OrderSide.Sell,
                        Quantity = positionSize,
                        Price = _currentPrices[signal.Symbol],
                        StopLoss = CalculateStopLoss(signal, riskMetrics),
                        TakeProfit = CalculateTakeProfit(signal, riskMetrics),
                        UseTwap = positionSize > 0.1m * _currentPrices[signal.Symbol],
                        TimeFrame = signal.TimeFrame
                    });

                    if (orderResult.Success)
                    {
                        var trade = new TradeRecord
                        {
                            Symbol = signal.Symbol,
                            Side = signal.Direction == TradeDirection.Long ? "BUY" : "SELL",
                            Quantity = orderResult.Quantity,
                            EntryPrice = orderResult.AveragePrice,
                            EntryTime = DateTime.UtcNow,
                            StopLoss = orderResult.StopLoss,
                            TakeProfit = orderResult.TakeProfit,
                            Commission = orderResult.Commission,
                            Slippage = orderResult.Slippage,
                            Volatility = riskMetrics.Volatility,
                            Liquidity = riskMetrics.Liquidity,
                            TimeFrame = signal.TimeFrame.ToString()
                        };

                        _portfolioManager.RecordTrade(trade);
                        _tradeHistory.Enqueue(trade);
                        await SaveTrade(trade);

                        // Добавление открытой позиции
                        _openPositions.Add(new OpenPosition
                        {
                            Symbol = signal.Symbol,
                            Quantity = orderResult.Quantity,
                            EntryPrice = orderResult.AveragePrice,
                            EntryTime = DateTime.UtcNow,
                            StopLoss = orderResult.StopLoss,
                            TakeProfit = orderResult.TakeProfit,
                            Direction = signal.Direction,
                            StopLossDistance = Math.Abs(orderResult.AveragePrice - orderResult.StopLoss)
                        });

                        // Обновление счетчиков убытков
                        if (orderResult.Profit < 0)
                        {
                            _consecutiveLosses++;
                            if (_consecutiveLosses >= _circuitBreakerThreshold)
                            {
                                _circuitBreakerTriggered = true;
                                await Notify($"⛔ Автостоп: {_consecutiveLosses} убыточных сделок подряд");
                            }
                        }
                        else
                        {
                            _consecutiveLosses = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка исполнения сделки для {signal.Symbol}");
                    await Notify($"⚠️ Ошибка сделки: {signal.Symbol} - {ex.Message}");
                }
            }
        }

        private decimal CalculateStopLoss(TradingSignal signal, RiskMetrics riskMetrics)
        {
            var atr = _marketDataProcessor.GetLatestAtr(signal.TimeFrame);
            var stopDistance = atr * 1.5m * (1 + riskMetrics.Volatility);

            return signal.Direction == TradeDirection.Long
                ? _currentPrices[signal.Symbol] - stopDistance
                : _currentPrices[signal.Symbol] + stopDistance;
        }

        private decimal CalculateTakeProfit(TradingSignal signal, RiskMetrics riskMetrics)
        {
            var entry = _currentPrices[signal.Symbol];
            var stopLoss = CalculateStopLoss(signal, riskMetrics);
            var risk = Math.Abs(entry - stopLoss);

            return signal.Direction == TradeDirection.Long
                ? entry + risk * 2m
                : entry - risk * 2m;
        }

        private async Task InitializeDatabase()
        {
            using var cmd = _dbConnection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Trades (
                    Id INTEGER PRIMARY KEY,
                    Symbol TEXT NOT NULL,
                    Side TEXT NOT NULL,
                    Quantity REAL NOT NULL,
                    EntryPrice REAL NOT NULL,
                    ExitPrice REAL,
                    StopLoss REAL,
                    TakeProfit REAL,
                    EntryTime DATETIME NOT NULL,
                    ExitTime DATETIME,
                    Profit REAL,
                    Commission REAL,
                    IsSuccessful INTEGER,
                    RiskPercent REAL,
                    Volatility REAL,
                    Liquidity REAL,
                    Slippage REAL,
                    TimeFrame TEXT,
                    ExitReason TEXT
                );
                
                CREATE TABLE IF NOT EXISTS MarketData (
                    Timestamp DATETIME PRIMARY KEY,
                    Symbol TEXT NOT NULL,
                    TimeFrame TEXT NOT NULL,
                    Open REAL NOT NULL,
                    High REAL NOT NULL,
                    Low REAL NOT NULL,
                    Close REAL NOT NULL,
                    Volume REAL NOT NULL,
                    RSI REAL,
                    MACD REAL,
                    ATR REAL
                );
                
                CREATE TABLE IF NOT EXISTS BotState (
                    Balance REAL NOT NULL,
                    LastTradeTime DATETIME,
                    EmergencyStop INTEGER DEFAULT 0,
                    ConsecutiveLosses INTEGER DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS NewsEvents (
                    Id INTEGER PRIMARY KEY,
                    Symbol TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    ImpactLevel INTEGER NOT NULL,
                    PublishedAt DATETIME NOT NULL,
                    ExpiresAt DATETIME NOT NULL
                );";

            await cmd.ExecuteNonQueryAsync();
        }

        private async Task LoadBotState()
        {
            using var cmd = _dbConnection.CreateCommand();
            cmd.CommandText = "SELECT Balance, LastTradeTime, EmergencyStop, ConsecutiveLosses FROM BotState LIMIT 1";

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                _currentBalance = reader.GetDecimal(0);
                _lastTradeTime = reader.GetDateTime(1);
                _circuitBreakerTriggered = reader.GetBoolean(2);
                _consecutiveLosses = reader.GetInt32(3);
            }
            else
            {
                _currentBalance = _initialBalance;
                _lastTradeTime = DateTime.UtcNow;

                cmd.CommandText = "INSERT INTO BotState (Balance, LastTradeTime) VALUES (@balance, @time)";
                cmd.Parameters.AddWithValue("@balance", _currentBalance);
                cmd.Parameters.AddWithValue("@time", _lastTradeTime);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private async Task ConnectWebSockets()
        {
            // Подписка на свечные данные для всех таймфреймов
            foreach (var timeFrame in _timeFrames)
            {
                var klineResult = await _socketClient.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                    Symbol,
                    timeFrame,
                    data =>
                    {
                        var kline = data.Data;
                        _marketDataProcessor.ProcessKline(new MarketDataPoint
                        {
                            Symbol = Symbol,
                            TimeFrame = timeFrame,
                            OpenTime = kline.Data.OpenTime,
                            Open = kline.Data.OpenPrice,
                            High = kline.Data.HighPrice,
                            Low = kline.Data.LowPrice,
                            Close = kline.Data.ClosePrice,
                            Volume = kline.Data.Volume,
                            IsClosed = kline.Data.Final
                        });

                        _currentPrices[Symbol] = kline.Data.ClosePrice;
                    });

                if (!klineResult.Success)
                    throw new Exception($"Ошибка подписки на свечи {timeFrame}: {klineResult.Error}");
            }

            // Подписка на стакан цен
            var bookResult = await _socketClient.SpotApi.ExchangeData.SubscribeToOrderBookUpdatesAsync(
                Symbol,
                1000,
                data =>
                {
                    _orderBooks[Symbol] = new OrderBook
                    {
                        Bids = data.Data.Bids.Select(b => new OrderBookEntry(b.Price, b.Quantity)).ToList(),
                        Asks = data.Data.Asks.Select(a => new OrderBookEntry(a.Price, a.Quantity)).ToList(),
                        Timestamp = data.ReceiveTime
                    };
                });

            if (!bookResult.Success)
                throw new Exception($"Ошибка подписки на стакан: {bookResult.Error}");

            // Подписка на пользовательские данные
            var listenKeyResult = await _restClient.SpotApi.Account.StartUserStreamAsync();
            if (!listenKeyResult.Success)
                throw new Exception($"Ошибка получения listen key: {listenKeyResult.Error}");

            var userDataResult = await _socketClient.SpotApi.Account.SubscribeToUserDataUpdatesAsync(
                listenKeyResult.Data,
                onOrderUpdate: data => _executionEngine.ProcessOrderUpdate(data.Data),
                onAccountUpdate: data => _portfolioManager.UpdateBalance(data.Data.Balances));

            if (!userDataResult.Success)
                throw new Exception($"Ошибка подписки на пользовательские данные: {userDataResult.Error}");
        }

        private async Task SaveTrade(TradeRecord trade)
        {
            using var cmd = _dbConnection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Trades (
                    Symbol, Side, Quantity, EntryPrice, ExitPrice, StopLoss, TakeProfit,
                    EntryTime, ExitTime, Profit, Commission, IsSuccessful, RiskPercent,
                    Volatility, Liquidity, Slippage, TimeFrame, ExitReason
                ) VALUES (
                    @symbol, @side, @qty, @entry, @exit, @sl, @tp,
                    @entryTime, @exitTime, @profit, @commission, @success, @risk,
                    @volatility, @liquidity, @slippage, @timeFrame, @exitReason
                )";

            cmd.Parameters.AddWithValue("@symbol", trade.Symbol);
            cmd.Parameters.AddWithValue("@side", trade.Side);
            cmd.Parameters.AddWithValue("@qty", trade.Quantity);
            cmd.Parameters.AddWithValue("@entry", trade.EntryPrice);
            cmd.Parameters.AddWithValue("@exit", trade.ExitPrice ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@sl", trade.StopLoss);
            cmd.Parameters.AddWithValue("@tp", trade.TakeProfit);
            cmd.Parameters.AddWithValue("@entryTime", trade.EntryTime);
            cmd.Parameters.AddWithValue("@exitTime", trade.ExitTime ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@profit", trade.Profit ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@commission", trade.Commission);
            cmd.Parameters.AddWithValue("@success", trade.IsSuccessful);
            cmd.Parameters.AddWithValue("@risk", trade.RiskPercent);
            cmd.Parameters.AddWithValue("@volatility", trade.Volatility);
            cmd.Parameters.AddWithValue("@liquidity", trade.Liquidity);
            cmd.Parameters.AddWithValue("@slippage", trade.Slippage);
            cmd.Parameters.AddWithValue("@timeFrame", trade.TimeFrame ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@exitReason", trade.ExitReason ?? (object)DBNull.Value);

            await cmd.ExecuteNonQueryAsync();

            _currentBalance = _portfolioManager.CurrentBalance;
            await UpdateBotState();
        }

        private async Task UpdateBotState()
        {
            using var cmd = _dbConnection.CreateCommand();
            cmd.CommandText = @"
                UPDATE BotState SET 
                    Balance = @balance, 
                    LastTradeTime = @time, 
                    EmergencyStop = @emergency,
                    ConsecutiveLosses = @losses";

            cmd.Parameters.AddWithValue("@balance", _currentBalance);
            cmd.Parameters.AddWithValue("@time", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@emergency", _circuitBreakerTriggered);
            cmd.Parameters.AddWithValue("@losses", _consecutiveLosses);

            await cmd.ExecuteNonQueryAsync();
        }

        private async Task Notify(string message)
        {
            if (_telegramBot == null) return;

            try
            {
                var chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");
                if (!string.IsNullOrEmpty(chatId))
                {
                    await _telegramBot.SendTextMessageAsync(chatId, message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка отправки уведомления");
            }
        }

        private async Task Cleanup()
        {
            try
            {
                _isRunning = false;
                await Notify("🔴 Торговый бот остановлен");

                // Закрытие всех открытых позиций
                foreach (var position in _openPositions.ToList())
                {
                    await ClosePosition(position, "Bot shutdown");
                }

                await _socketClient.UnsubscribeAllAsync();
                _newsMonitor.StopMonitoring();

                if (_dbConnection.State == ConnectionState.Open)
                {
                    await _dbConnection.CloseAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при завершении работы");
            }
        }

        public void Dispose()
        {
            Cleanup().Wait();
            _restClient?.Dispose();
            _socketClient?.Dispose();
            _dbConnection?.Dispose();
            GC.SuppressFinalize(this);
        }

        #region Enhanced Components
        public enum MarketTrend { Bullish, Bearish, Neutral }

        public class EnhancedRiskEngine
        {
            private readonly decimal _maxDrawdown;
            private readonly decimal _maxRiskPerTrade;
            private readonly decimal _kellyFraction;
            private readonly decimal _maxLeverage;
            private readonly decimal _varConfidenceLevel;
            private readonly decimal _maxVolatility;
            private readonly decimal _minLiquidity;
            private readonly int _maxConsecutiveLosses;
            private readonly ILogger _logger;

            public EnhancedRiskEngine(
                decimal maxDrawdown,
                decimal maxRiskPerTrade,
                decimal kellyFraction,
                decimal maxLeverage,
                decimal varConfidenceLevel,
                decimal maxVolatility,
                decimal minLiquidity,
                int maxConsecutiveLosses,
                ILogger logger = null)
            {
                _maxDrawdown = maxDrawdown;
                _maxRiskPerTrade = maxRiskPerTrade;
                _kellyFraction = kellyFraction;
                _maxLeverage = maxLeverage;
                _varConfidenceLevel = varConfidenceLevel;
                _maxVolatility = maxVolatility;
                _minLiquidity = minLiquidity;
                _maxConsecutiveLosses = maxConsecutiveLosses;
                _logger = logger;
            }

            public bool IsTradeAllowed(TradingSignal signal, RiskMetrics riskMetrics, MarketTrend marketTrend)
            {
                try
                {
                    // Проверка волатильности
                    if (riskMetrics.Volatility > _maxVolatility)
                    {
                        _logger?.LogInformation($"Торговля заблокирована: волатильность {riskMetrics.Volatility:P2} > {_maxVolatility:P2}");
                        return false;
                    }

                    // Проверка ликвидности
                    if (riskMetrics.Liquidity < _minLiquidity)
                    {
                        _logger?.LogInformation($"Торговля заблокирована: ликвидность {riskMetrics.Liquidity:F2} < {_minLiquidity:F2}");
                        return false;
                    }

                    // Проверка риска портфеля
                    if (riskMetrics.PortfolioRisk > _maxRiskPerTrade)
                    {
                        _logger?.LogInformation($"Торговля заблокирована: риск портфеля {riskMetrics.PortfolioRisk:P2} > {_maxRiskPerTrade:P2}");
                        return false;
                    }

                    // Проверка CVaR
                    if (riskMetrics.CVaR > _maxDrawdown)
                    {
                        _logger?.LogInformation($"Торговля заблокирована: CVaR {riskMetrics.CVaR:P2} > {_maxDrawdown:P2}");
                        return false;
                    }

                    // Проверка на тренд (избегаем торговли против сильного тренда)
                    if ((signal.Direction == TradeDirection.Long && marketTrend == MarketTrend.Bearish) ||
                        (signal.Direction == TradeDirection.Short && marketTrend == MarketTrend.Bullish))
                    {
                        _logger?.LogInformation($"Торговля заблокирована: сигнал против тренда");
                        return false;
                    }

                    // Проверка корреляции с открытыми позициями
                    foreach (var pos in riskMetrics.OpenPositions)
                    {
                        var correlation = riskMetrics.CorrelationMatrix[signal.Symbol][pos.Symbol];
                        if (correlation > 0.8m)
                        {
                            _logger?.LogInformation($"Торговля заблокирована: высокая корреляция ({correlation:P2}) с {pos.Symbol}");
                            return false;
                        }
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка проверки допустимости сделки");
                    return false;
                }
            }

            public decimal CalculatePositionSize(
                TradingSignal signal,
                decimal currentPrice,
                RiskMetrics riskMetrics)
            {
                try
                {
                    // Параметры стратегии (должны настраиваться на основе бэктеста)
                    const decimal winRate = 0.55m;
                    const decimal avgWin = 0.03m;
                    const decimal avgLoss = 0.015m;

                    // Расчет критерия Келли
                    var kelly = winRate - ((1 - winRate) / (avgWin / avgLoss));

                    // Учет текущего риска портфеля
                    var fraction = kelly * _kellyFraction * (1 - riskMetrics.PortfolioRisk);

                    // Минимальный и максимальный размер позиции
                    var minSize = 0.001m; // Минимальный допустимый размер
                    var maxSize = riskMetrics.Liquidity * 0.1m / currentPrice; // Не более 10% от ликвидности

                    // Расчет размера позиции
                    var size = (riskMetrics.PortfolioValue * fraction) / currentPrice;

                    // Учет волатильности (уменьшаем размер при высокой волатильности)
                    size *= 1 - (riskMetrics.Volatility / _maxVolatility);

                    return Math.Clamp(size, minSize, maxSize);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета размера позиции");
                    return 0m;
                }
            }

            public decimal CalculatePortfolioRisk(
                List<OpenPosition> positions,
                ConcurrentDictionary<string, decimal> currentPrices,
                ConcurrentDictionary<string, decimal> volatilities)
            {
                if (!positions.Any()) return 0m;

                try
                {
                    decimal totalRisk = 0;
                    decimal totalValue = 0;

                    foreach (var position in positions)
                    {
                        var price = currentPrices.GetValueOrDefault(position.Symbol, 0m);
                        if (price == 0m) continue;

                        var value = position.Quantity * price;
                        var volatility = volatilities.GetValueOrDefault(position.Symbol, 0m);

                        totalRisk += value * volatility;
                        totalValue += value;
                    }

                    return totalValue > 0 ? totalRisk / totalValue : 0m;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета риска портфеля");
                    return 1m; // В случае ошибки возвращаем максимальный риск
                }
            }

            public decimal CalculateCVaR(List<TradeRecord> trades, decimal confidenceLevel)
            {
                if (trades.Count < 50) return 0m;

                try
                {
                    var losses = trades
                        .Where(t => t.Profit < 0)
                        .Select(t => -t.Profit.Value)
                        .OrderByDescending(l => l)
                        .ToList();

                    if (!losses.Any()) return 0m;

                    var index = (int)(losses.Count * (1 - confidenceLevel));
                    return index > 0 ? (decimal)losses.Take(index).Average() : 0m;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета CVaR");
                    return 0m;
                }
            }
        }

        public class EnhancedExecutionEngine
        {
            private readonly IBinanceRestClient _restClient;
            private readonly IBinanceSocketClient _socketClient;
            private readonly AsyncRetryPolicy _retryPolicy;
            private readonly ILogger _logger;
            private readonly bool _isSandbox;
            private readonly ConcurrentDictionary<long, BinanceOrder> _activeOrders = new();

            public EnhancedExecutionEngine(
                IBinanceRestClient restClient,
                IBinanceSocketClient socketClient,
                AsyncRetryPolicy retryPolicy,
                ILogger logger,
                bool isSandbox)
            {
                _restClient = restClient;
                _socketClient = socketClient;
                _retryPolicy = retryPolicy;
                _logger = logger;
                _isSandbox = isSandbox;
            }

            public async Task<TradeResult> ExecuteOrder(OrderRequest request)
            {
                try
                {
                    // Для песочницы уменьшаем размер ордера
                    if (_isSandbox)
                    {
                        request.Quantity = Math.Min(request.Quantity, SandboxTestAmount / request.Price);
                    }

                    if (request.UseTwap && request.Quantity > 0.1m)
                    {
                        return await ExecuteTwapOrder(request);
                    }

                    return await ExecuteInstantOrder(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка исполнения ордера");
                    return new TradeResult { Success = false, Error = ex.Message };
                }
            }

            public async Task<TradeResult> ClosePosition(OpenPosition position)
            {
                try
                {
                    var orderResult = await _retryPolicy.ExecuteAsync(async () =>
                    {
                        var order = new BinancePlaceOrderRequest
                        {
                            Symbol = position.Symbol,
                            Side = position.Direction == TradeDirection.Long ?
                                OrderSide.Sell : OrderSide.Buy,
                            Type = SpotOrderType.Market,
                            Quantity = position.Quantity,
                            NewClientOrderId = $"close_{DateTime.UtcNow.Ticks}"
                        };

                        return await _restClient.SpotApi.Trading.PlaceOrderAsync(order);
                    });

                    if (!orderResult.Success)
                    {
                        return new TradeResult
                        {
                            Success = false,
                            Error = orderResult.Error?.Message
                        };
                    }

                    return new TradeResult
                    {
                        Success = true,
                        Symbol = position.Symbol,
                        Side = position.Direction == TradeDirection.Long ? "SELL" : "BUY",
                        Quantity = orderResult.Data.QuantityFilled,
                        AveragePrice = orderResult.Data.Price,
                        Commission = orderResult.Data.Fee,
                        Slippage = 0 // Для рыночных ордеров slippage рассчитывается иначе
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка закрытия позиции {position.Symbol}");
                    return new TradeResult { Success = false, Error = ex.Message };
                }
            }

            private async Task<TradeResult> ExecuteTwapOrder(OrderRequest request)
            {
                const int chunks = 5;
                decimal chunkSize = request.Quantity / chunks;
                decimal remaining = request.Quantity;
                decimal totalFilled = 0;
                decimal totalCost = 0;
                decimal totalCommission = 0;
                decimal totalSlippage = 0;

                for (int i = 0; i < chunks && remaining > 0; i++)
                {
                    await Task.Delay(1000);

                    var orderResult = await _retryPolicy.ExecuteAsync(async () =>
                    {
                        var order = new BinancePlaceOrderRequest
                        {
                            Symbol = request.Symbol,
                            Side = request.Side,
                            Type = SpotOrderType.Market,
                            Quantity = Math.Min(chunkSize, remaining),
                            NewClientOrderId = $"twap_{i}_{DateTime.UtcNow.Ticks}"
                        };

                        return await _restClient.SpotApi.Trading.PlaceOrderAsync(order);
                    });

                    if (!orderResult.Success)
                    {
                        _logger.LogError($"TWAP часть {i} не исполнена: {orderResult.Error}");
                        continue;
                    }

                    decimal filled = orderResult.Data.QuantityFilled;
                    decimal avgPrice = orderResult.Data.Price;
                    decimal commission = orderResult.Data.Fee;

                    totalFilled += filled;
                    totalCost += filled * avgPrice;
                    totalCommission += commission;
                    totalSlippage += Math.Abs(avgPrice - request.Price) * filled;
                    remaining -= filled;
                }

                if (totalFilled == 0)
                {
                    return new TradeResult { Success = false, Error = "Не удалось исполнить ни одной части TWAP" };
                }

                return new TradeResult
                {
                    Success = true,
                    Symbol = request.Symbol,
                    Side = request.Side.ToString(),
                    Quantity = totalFilled,
                    AveragePrice = totalCost / totalFilled,
                    StopLoss = request.StopLoss,
                    TakeProfit = request.TakeProfit,
                    EntryTime = DateTime.UtcNow,
                    Commission = totalCommission,
                    Slippage = totalSlippage
                };
            }

            private async Task<TradeResult> ExecuteInstantOrder(OrderRequest request)
            {
                var orderResult = await _retryPolicy.ExecuteAsync(async () =>
                {
                    var order = new BinancePlaceOrderRequest
                    {
                        Symbol = request.Symbol,
                        Side = request.Side,
                        Type = SpotOrderType.Limit,
                        Quantity = request.Quantity,
                        Price = request.Price,
                        TimeInForce = TimeInForce.GoodTillCanceled,
                        StopPrice = request.StopLoss,
                        StopLimitPrice = request.StopLoss * 0.995m, // На 0.5% ниже стоп-цены
                        NewClientOrderId = $"limit_{DateTime.UtcNow.Ticks}"
                    };

                    return await _restClient.SpotApi.Trading.PlaceOrderAsync(order);
                });

                if (!orderResult.Success)
                {
                    _logger.LogError($"Ошибка ордера: {orderResult.Error}");
                    return new TradeResult { Success = false, Error = orderResult.Error?.Message };
                }

                var executionResult = await WaitForOrderExecution(request.Symbol, orderResult.Data.Id);
                return MapToTradeResult(request, executionResult);
            }

            private async Task<BinanceOrder> WaitForOrderExecution(string symbol, long orderId)
            {
                var timeout = DateTime.UtcNow.Add(OrderTimeout);
                _activeOrders.TryAdd(orderId, null);

                try
                {
                    while (DateTime.UtcNow < timeout)
                    {
                        var orderResult = await _retryPolicy.ExecuteAsync(() =>
                            _restClient.SpotApi.Trading.GetOrderAsync(symbol, orderId));

                        if (orderResult.Success)
                        {
                            if (orderResult.Data.Status == OrderStatus.Filled)
                                return orderResult.Data;

                            if (orderResult.Data.Status.IsFinal())
                                throw new Exception($"Ордер завершен со статусом: {orderResult.Data.Status}");
                        }

                        await Task.Delay(1000);
                    }

                    // Отмена ордера по таймауту
                    var cancelResult = await _restClient.SpotApi.Trading.CancelOrderAsync(symbol, orderId);
                    if (!cancelResult.Success)
                    {
                        _logger.LogError($"Не удалось отменить ордер {orderId}: {cancelResult.Error}");
                    }

                    throw new TimeoutException("Исполнение ордера превысило таймаут");
                }
                finally
                {
                    _activeOrders.TryRemove(orderId, out _);
                }
            }

            private TradeResult MapToTradeResult(OrderRequest request, BinanceOrder order) =>
                new()
                {
                    Success = true,
                    Symbol = request.Symbol,
                    Side = request.Side.ToString(),
                    Quantity = order.QuantityFilled,
                    AveragePrice = order.Price,
                    StopLoss = request.StopLoss,
                    TakeProfit = request.TakeProfit,
                    EntryTime = DateTime.UtcNow,
                    Commission = order.Fee,
                    Slippage = Math.Abs(order.Price - request.Price) * order.QuantityFilled
                };

            public void ProcessOrderUpdate(BinanceStreamOrderUpdate update)
            {
                if (_activeOrders.TryGetValue(update.Data.Id, out var order))
                {
                    // Обновление статуса ордера в реальном времени
                    _activeOrders[update.Data.Id] = update.Data;

                    if (update.Data.Status.IsFinal())
                    {
                        _activeOrders.TryRemove(update.Data.Id, out _);
                    }
                }
            }

            public async Task<decimal> MeasureLatency()
            {
                var sw = Stopwatch.StartNew();
                await _restClient.SpotApi.ExchangeData.GetServerTimeAsync();
                return sw.ElapsedMilliseconds;
            }
        }

        public class MultiTimeFrameMarketDataProcessor
        {
            private readonly Dictionary<KlineInterval, List<MarketDataPoint>> _dataCache = new();
            private readonly object _cacheLock = new();
            private readonly List<KlineInterval> _timeFrames;

            public MultiTimeFrameMarketDataProcessor(List<KlineInterval> timeFrames)
            {
                _timeFrames = timeFrames;
                foreach (var tf in timeFrames)
                {
                    _dataCache[tf] = new List<MarketDataPoint>();
                }
            }

            public void ProcessKline(MarketDataPoint data)
            {
                lock (_cacheLock)
                {
                    if (!_dataCache.ContainsKey(data.TimeFrame))
                        return;

                    _dataCache[data.TimeFrame].Add(data);

                    // Ограничение размера кэша
                    if (_dataCache[data.TimeFrame].Count > 5000)
                    {
                        _dataCache[data.TimeFrame].RemoveRange(0, _dataCache[data.TimeFrame].Count - 5000);
                    }
                }
            }

            public List<MarketDataPoint> GetLatestData(KlineInterval timeFrame)
            {
                lock (_cacheLock)
                {
                    return _dataCache.TryGetValue(timeFrame, out var data) ?
                        new List<MarketDataPoint>(data) :
                        new List<MarketDataPoint>();
                }
            }

            public void CalculateIndicators(MarketDataPoint data)
            {
                try
                {
                    var timeFrameData = GetLatestData(data.TimeFrame);
                    if (timeFrameData.Count < 50) return;

                    double[] closes = timeFrameData.Select(d => (double)d.Close).ToArray();
                    double[] highs = timeFrameData.Select(d => (double)d.High).ToArray();
                    double[] lows = timeFrameData.Select(d => (double)d.Low).ToArray();

                    // RSI
                    double[] rsiOutput = new double[closes.Length];
                    Core.Rsi(closes, 0, closes.Length - 1, rsiOutput, out _, out _);
                    data.RSI = (decimal)rsiOutput.Last();

                    // MACD
                    double[] macd = new double[closes.Length];
                    double[] signal = new double[closes.Length];
                    double[] hist = new double[closes.Length];
                    Core.Macd(closes, 0, closes.Length - 1, macd, signal, hist, out _, out _);
                    data.MACD = (decimal)macd.Last();
                    data.Signal = (decimal)signal.Last();

                    // ATR
                    double[] atrOutput = new double[closes.Length];
                    Core.Atr(highs, lows, closes, 0, closes.Length - 1, atrOutput, out _, out _);
                    data.ATR = (decimal)atrOutput.Last();

                    // SMA 50 и 200
                    double[] sma50 = new double[closes.Length];
                    Core.Sma(closes, 0, closes.Length - 1, sma50, out _, out _, 50);
                    data.SMA50 = (decimal)sma50.Last();

                    double[] sma200 = new double[closes.Length];
                    Core.Sma(closes, 0, closes.Length - 1, sma200, out _, out _, 200);
                    data.SMA200 = (decimal)sma200.Last();
                }
                catch (Exception ex)
                {
                    // Логирование ошибок расчета индикаторов
                    Debug.WriteLine($"Ошибка расчета индикаторов: {ex.Message}");
                }
            }

            public TradingSignal GenerateTaSignal(MarketDataPoint data)
            {
                try
                {
                    // Определение направления на основе нескольких индикаторов
                    bool isBullish = data.RSI > 50m &&
                                    data.MACD > data.Signal &&
                                    data.Close > data.SMA50 &&
                                    data.SMA50 > data.SMA200;

                    bool isBearish = data.RSI < 50m &&
                                     data.MACD < data.Signal &&
                                     data.Close < data.SMA50 &&
                                     data.SMA50 < data.SMA200;

                    var direction = isBullish ? TradeDirection.Long :
                                    isBearish ? TradeDirection.Short :
                                    data.RSI > 50m ? TradeDirection.Long : TradeDirection.Short;

                    // Расчет уверенности сигнала
                    var confidenceFactors = new List<decimal>
                    {
                        Math.Abs(data.RSI - 50m) / 50m, // Уверенность по RSI
                        Math.Abs(data.MACD - data.Signal) / (data.Signal != 0 ? data.Signal : 1m), // Уверенность по MACD
                        (data.Close - data.SMA50) / data.SMA50 * 10m // Отклонение от SMA50
                    };

                    var confidence = confidenceFactors.Average();

                    return new TradingSignal
                    {
                        Symbol = data.Symbol,
                        Direction = direction,
                        Confidence = Math.Min(1m, confidence),
                        Timestamp = DateTime.UtcNow,
                        TimeFrame = data.TimeFrame
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка генерации сигнала: {ex.Message}");
                    return new TradingSignal
                    {
                        Symbol = data.Symbol,
                        Direction = TradeDirection.Long,
                        Confidence = 0,
                        Timestamp = DateTime.UtcNow,
                        TimeFrame = data.TimeFrame
                    };
                }
            }

            public MarketTrend DetermineMarketTrend(List<MarketDataPoint> marketData)
            {
                try
                {
                    var dailyData = marketData
                        .Where(d => d.TimeFrame == KlineInterval.OneDay)
                        .OrderBy(d => d.OpenTime)
                        .ToList();

                    if (dailyData.Count < 30) return MarketTrend.Neutral;

                    // Анализ SMA
                    var sma50 = dailyData.TakeLast(50).Average(d => d.Close);
                    var sma200 = dailyData.TakeLast(200).Average(d => d.Close);

                    // Анализ последних 5 дней
                    var last5Days = dailyData.TakeLast(5).ToList();
                    var bullDays = last5Days.Count(d => d.Close > d.Open);
                    var bearDays = last5Days.Count(d => d.Close < d.Open);

                    // Определение тренда
                    if (sma50 > sma200 && bullDays > bearDays)
                        return MarketTrend.Bullish;

                    if (sma50 < sma200 && bearDays > bullDays)
                        return MarketTrend.Bearish;

                    return MarketTrend.Neutral;
                }
                catch
                {
                    return MarketTrend.Neutral;
                }
            }

            public decimal CalculateVolatility(List<MarketDataPoint> data, int lookbackPeriod)
            {
                if (data.Count < lookbackPeriod) return 0m;

                try
                {
                    var returns = new List<decimal>();
                    for (int i = 1; i < lookbackPeriod; i++)
                    {
                        returns.Add((data[i].Close - data[i - 1].Close) / data[i - 1].Close);
                    }

                    var mean = returns.Average();
                    var sumOfSquares = returns.Sum(r => Math.Pow((double)(r - mean), 2));
                    var stdDev = Math.Sqrt(sumOfSquares / returns.Count);

                    return (decimal)stdDev;
                }
                catch
                {
                    return 0m;
                }
            }

            public decimal GetLatestAtr(KlineInterval timeFrame)
            {
                lock (_cacheLock)
                {
                    return _dataCache.TryGetValue(timeFrame, out var data) && data.Count > 0 ?
                        data.Last().ATR : 0m;
                }
            }
        }

        public class OnlineModelTrainer
        {
            private readonly MLContext _mlContext;
            private readonly int _lookbackWindow;
            private ITransformer _model;
            private PredictionEngine<MarketDataPoint, PricePrediction> _predictionEngine;
            private readonly ILogger _logger;

            public OnlineModelTrainer(
                MLContext mlContext,
                int lookbackWindow,
                ILogger logger = null)
            {
                _mlContext = mlContext;
                _lookbackWindow = lookbackWindow;
                _logger = logger;
                InitializeModel();
            }

            private void InitializeModel()
            {
                try
                {
                    var emptyData = _mlContext.Data.LoadFromEnumerable(new List<MarketDataPoint>());

                    var pipeline = _mlContext.Transforms.Concatenate("Features",
                            nameof(MarketDataPoint.RSI),
                            nameof(MarketDataPoint.MACD),
                            nameof(MarketDataPoint.ATR),
                            nameof(MarketDataPoint.Volume))
                        .Append(_mlContext.Regression.Trainers.LightGbm(
                            new Microsoft.ML.Trainers.LightGbm.LightGbmRegressionTrainer.Options
                            {
                                NumberOfIterations = 100,
                                LearningRate = 0.1,
                                NumberOfLeaves = 20,
                                MinimumExampleCountPerLeaf = 10,
                                UseCategoricalSplit = true,
                                HandleMissingValue = true,
                                UseZeroAsMissingValue = false,
                                MinimumExampleCountPerGroup = 50,
                                MaximumCategoricalSplitPointCount = 16,
                                CategoricalSmoothing = 10,
                                L2CategoricalRegularization = 0.5
                            }));

                    _model = pipeline.Fit(emptyData);
                    _predictionEngine = _mlContext.Model.CreatePredictionEngine<MarketDataPoint, PricePrediction>(_model);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка инициализации модели ML");
                    throw;
                }
            }

            public async Task UpdateModels(List<MarketDataPoint> newData)
            {
                if (newData.Count < _lookbackWindow) return;

                try
                {
                    var dataView = _mlContext.Data.LoadFromEnumerable(newData);
                    var newModel = await Task.Run(() =>
                    {
                        // Проверка на переобучение
                        var cvResults = _mlContext.Regression.CrossValidate(
                            dataView,
                            _model.GetPipeline(),
                            numberOfFolds: 5);

                        var avgRSquared = cvResults.Average(r => r.Metrics.RSquared);
                        if (avgRSquared < 0.7)
                        {
                            _logger?.LogWarning($"Возможно переобучение модели. R²: {avgRSquared:F2}");
                        }

                        return _model.ContinueTrain(dataView);
                    });

                    Interlocked.Exchange(ref _model, newModel);
                    _predictionEngine = _mlContext.Model.CreatePredictionEngine<MarketDataPoint, PricePrediction>(_model);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка обновления модели");
                }
            }

            public TradingSignal Predict(MarketDataPoint data)
            {
                try
                {
                    var prediction = _predictionEngine.Predict(data);
                    return new TradingSignal
                    {
                        Symbol = data.Symbol,
                        Direction = prediction.FuturePriceChange > 0 ? TradeDirection.Long : TradeDirection.Short,
                        Confidence = Math.Min(1m, Math.Abs((decimal)prediction.FuturePriceChange)),
                        Timestamp = DateTime.UtcNow,
                        TimeFrame = data.TimeFrame,
                        Features = new Dictionary<string, object>
                        {
                            { "RSI", data.RSI },
                            { "MACD", data.MACD },
                            { "ATR", data.ATR },
                            { "Volume", data.Volume }
                        }
                    };
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка предсказания");
                    return new TradingSignal
                    {
                        Symbol = data.Symbol,
                        Direction = TradeDirection.Long,
                        Confidence = 0,
                        Timestamp = DateTime.UtcNow,
                        TimeFrame = data.TimeFrame
                    };
                }
            }
        }

        public class Backtester
        {
            private readonly IBinanceRestClient _restClient;
            private readonly MultiTimeFrameMarketDataProcessor _marketDataProcessor;
            private readonly ILogger _logger;
            private readonly List<KlineInterval> _timeFrames;

            public Backtester(
                IBinanceRestClient restClient,
                MultiTimeFrameMarketDataProcessor marketDataProcessor,
                ILogger logger,
                List<KlineInterval> timeFrames)
            {
                _restClient = restClient;
                _marketDataProcessor = marketDataProcessor;
                _logger = logger;
                _timeFrames = timeFrames;
            }

            public async Task<BacktestResult> RunBacktest(
                string symbol,
                KlineInterval interval,
                int lookbackDays,
                string? filter = null)
            {
                var endTime = DateTime.UtcNow;
                var startTime = endTime.AddDays(-lookbackDays);

                var klinesResult = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol, interval, startTime, endTime, limit: 1000);

                if (!klinesResult.Success)
                {
                    _logger.LogError($"Бэктест не удался: {klinesResult.Error}");
                    return new BacktestResult { Success = false };
                }

                var marketData = klinesResult.Data.Select(k => new MarketDataPoint
                {
                    Symbol = symbol,
                    TimeFrame = interval,
                    OpenTime = k.OpenTime,
                    Open = k.OpenPrice,
                    High = k.HighPrice,
                    Low = k.LowPrice,
                    Close = k.ClosePrice,
                    Volume = k.Volume,
                    IsClosed = true
                }).ToList();

                // Применение фильтров
                if (filter == "HighVolatility")
                {
                    marketData = marketData
                        .Where(d => (d.High - d.Low) / d.Close > 0.02m)
                        .ToList();
                }
                else if (filter == "LowLiquidity")
                {
                    // Эмуляция низкой ликвидности - увеличение проскальзывания
                    foreach (var d in marketData)
                    {
                        d.Close *= 1.001m; // Имитация проскальзывания
                    }
                }

                // Расчет индикаторов
                foreach (var data in marketData)
                {
                    _marketDataProcessor.CalculateIndicators(data);
                }

                // Параметры бэктеста
                decimal balance = 10000m;
                int wins = 0, losses = 0;
                decimal maxBalance = balance;
                decimal maxDrawdown = 0m;
                var returns = new List<decimal>();
                var trades = new List<TradeRecord>();

                // Имитация торговли
                for (int i = 50; i < marketData.Count - 1; i++)
                {
                    var current = marketData[i];
                    var prediction = _marketDataProcessor.GenerateTaSignal(current);

                    if (prediction.Confidence > 0.7m)
                    {
                        decimal entry = current.Close;
                        decimal exit = marketData[i + 1].Close;

                        // Эмуляция комиссий и проскальзывания
                        decimal commission = entry * 0.001m * 2;
                        decimal slippage = entry * 0.0005m;

                        decimal pnl = (exit - entry) * (prediction.Direction == TradeDirection.Long ? 1 : -1);
                        decimal netPnl = pnl - commission - slippage;

                        // Обновление баланса
                        balance += netPnl;
                        returns.Add(netPnl / 10000m);

                        if (netPnl > 0) wins++; else losses++;

                        // Обновление максимальной просадки
                        maxBalance = Math.Max(maxBalance, balance);
                        maxDrawdown = Math.Max(maxDrawdown, (maxBalance - balance) / maxBalance);

                        // Сохранение сделки для анализа
                        trades.Add(new TradeRecord
                        {
                            Symbol = symbol,
                            Side = prediction.Direction == TradeDirection.Long ? "BUY" : "SELL",
                            Quantity = 10000m / entry,
                            EntryPrice = entry,
                            ExitPrice = exit,
                            EntryTime = current.OpenTime,
                            ExitTime = marketData[i + 1].OpenTime,
                            Profit = netPnl,
                            Commission = commission,
                            Slippage = slippage,
                            TimeFrame = interval.ToString()
                        });
                    }
                }

                // Расчет метрик
                decimal sharpeRatio = CalculateSharpeRatio(returns);
                decimal winRate = wins + losses > 0 ? (decimal)wins / (wins + losses) : 0m;
                decimal totalReturn = (balance - 10000m) / 10000m;

                return new BacktestResult
                {
                    Success = true,
                    SharpeRatio = sharpeRatio,
                    TotalReturn = totalReturn,
                    MaxDrawdown = maxDrawdown,
                    WinRate = winRate,
                    TimeFrame = interval.ToString(),
                    Trades = trades
                };
            }

            private decimal CalculateSharpeRatio(List<decimal> returns)
            {
                if (returns.Count == 0) return 0m;

                try
                {
                    var avgReturn = returns.Average();
                    var stdDev = (decimal)Math.Sqrt(returns.Select(r => Math.Pow((double)(r - avgReturn), 2)).Average());

                    // Годовой Sharpe Ratio
                    return stdDev != 0 ? avgReturn / stdDev * (decimal)Math.Sqrt(365) : 0m;
                }
                catch
                {
                    return 0m;
                }
            }
        }

        public class NewsMonitor
        {
            private readonly ILogger _logger;
            private readonly ConcurrentDictionary<string, NewsEvent> _activeNews = new();
            private Timer _monitoringTimer;
            private bool _isMonitoring;

            public NewsMonitor(ILogger logger)
            {
                _logger = logger;
            }

            public void StartMonitoring()
            {
                _isMonitoring = true;
                _monitoringTimer = new Timer(CheckNews, null, TimeSpan.Zero, TimeSpan.FromMinutes(5));
                _logger.LogInformation("Мониторинг новостей запущен");
            }

            public void StopMonitoring()
            {
                _isMonitoring = false;
                _monitoringTimer?.Dispose();
                _logger.LogInformation("Мониторинг новостей остановлен");
            }

            private async void CheckNews(object state)
            {
                if (!_isMonitoring) return;

                try
                {
                    // Здесь должна быть реализация API для получения новостей
                    // Для примера используем заглушку
                    var newEvents = await FetchNewsEvents();

                    foreach (var evt in newEvents)
                    {
                        _activeNews[evt.Symbol] = evt;
                    }

                    // Удаление устаревших новостей
                    var expired = _activeNews.Where(kv => kv.Value.ExpiresAt < DateTime.UtcNow).ToList();
                    foreach (var kv in expired)
                    {
                        _activeNews.TryRemove(kv.Key, out _);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка мониторинга новостей");
                }
            }

            private Task<List<NewsEvent>> FetchNewsEvents()
            {
                // Заглушка - в реальной реализации здесь должен быть вызов API новостей
                return Task.FromResult(new List<NewsEvent>());
            }

            public bool IsHighImpactNewsPending()
            {
                return _activeNews.Any(kv => kv.Value.ImpactLevel >= 3);
            }

            public List<string> GetAffectedSymbols()
            {
                return _activeNews
                    .Where(kv => kv.Value.ImpactLevel >= 3)
                    .Select(kv => kv.Key)
                    .ToList();
            }

            public bool IsSymbolAffected(string symbol)
            {
                return _activeNews.ContainsKey(symbol) &&
                       _activeNews[symbol].ImpactLevel >= 2;
            }
        }

        public class PortfolioManager
        {
            private decimal _balance;
            private readonly List<TradeRecord> _trades = new();
            private readonly EnhancedRiskEngine _riskEngine;
            private readonly ILogger _logger;

            public decimal CurrentBalance => _balance;

            public PortfolioManager(
                decimal initialBalance,
                EnhancedRiskEngine riskEngine,
                ILogger logger = null)
            {
                _balance = initialBalance;
                _riskEngine = riskEngine;
                _logger = logger;
            }

            public async Task UpdateBalanceFromExchange(IBinanceRestClient restClient)
            {
                try
                {
                    var accountInfo = await restClient.SpotApi.Account.GetAccountInfoAsync();
                    if (accountInfo.Success)
                    {
                        var usdtBalance = accountInfo.Data.Balances.FirstOrDefault(b => b.Asset == "USDT");
                        if (usdtBalance != null)
                        {
                            _balance = usdtBalance.Total;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка обновления баланса с биржи");
                }
            }

            public void UpdateBalance(IEnumerable<BinanceBalance> balances)
            {
                var usdtBalance = balances.FirstOrDefault(b => b.Asset == "USDT");
                if (usdtBalance != null)
                {
                    _balance = usdtBalance.Total;
                }
            }

            public void RecordTrade(TradeRecord trade)
            {
                _trades.Add(trade);

                if (trade.ExitPrice.HasValue)
                {
                    var pnl = trade.Side == "BUY"
                        ? (trade.ExitPrice.Value - trade.EntryPrice) * trade.Quantity
                        : (trade.EntryPrice - trade.ExitPrice.Value) * trade.Quantity;

                    _balance += pnl - trade.Commission;
                }
            }

            public List<OpenPosition> GetOpenPositions() =>
                _trades
                    .Where(t => !t.ExitPrice.HasValue)
                    .Select(t => new OpenPosition
                    {
                        Symbol = t.Symbol,
                        Quantity = t.Quantity,
                        EntryPrice = t.EntryPrice,
                        EntryTime = t.EntryTime,
                        StopLoss = t.StopLoss,
                        TakeProfit = t.TakeProfit,
                        Direction = t.Side == "BUY" ? TradeDirection.Long : TradeDirection.Short,
                        StopLossDistance = Math.Abs(t.EntryPrice - t.StopLoss)
                    })
                    .ToList();

            public decimal CalculatePositionSize(
                TradingSignal signal,
                decimal currentPrice,
                RiskMetrics riskMetrics) =>
                _riskEngine.CalculatePositionSize(signal, currentPrice, riskMetrics);
        }

        public class CorrelationAnalyzer
        {
            private readonly MLContext _mlContext;
            private readonly List<string> _symbols;
            private readonly Dictionary<string, List<double>> _priceHistory;
            private readonly ILogger _logger;

            public CorrelationAnalyzer(List<string> symbols, ILogger logger = null)
            {
                _mlContext = new MLContext();
                _symbols = symbols;
                _logger = logger;
                _priceHistory = symbols.ToDictionary(s => s, _ => new List<double>());
            }

            public void AddPriceData(string symbol, decimal price)
            {
                try
                {
                    if (!_priceHistory.ContainsKey(symbol))
                        throw new ArgumentException($"Символ {symbol} не настроен");

                    _priceHistory[symbol].Add((double)price);

                    // Поддержание одинаковой длины для всех рядов
                    var minLength = _priceHistory.Values.Min(list => list.Count);
                    foreach (var key in _priceHistory.Keys.ToList())
                    {
                        _priceHistory[key] = _priceHistory[key]
                            .Skip(_priceHistory[key].Count - minLength)
                            .ToList();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Ошибка добавления данных цены для {symbol}");
                }
            }

            public Dictionary<string, Dictionary<string, decimal>> GetCorrelationMatrix()
            {
                try
                {
                    if (_priceHistory.Values.First().Count < 2)
                        return _symbols.ToDictionary(s => s, s => _symbols.ToDictionary(s2 => s2, s2 => 0m));

                    var data = new List<CorrelationDataItem>();
                    for (int i = 0; i < _priceHistory.Values.First().Count; i++)
                    {
                        var item = new CorrelationDataItem();
                        foreach (var symbol in _symbols)
                        {
                            typeof(CorrelationDataItem)
                                .GetProperty(symbol)
                                ?.SetValue(item, _priceHistory[symbol][i]);
                        }
                        data.Add(item);
                    }

                    var dataView = _mlContext.Data.LoadFromEnumerable(data);
                    var correlationMatrix = _mlContext.Data.ComputeCorrelation(dataView);

                    var matrix = new Dictionary<string, Dictionary<string, decimal>>();
                    for (int i = 0; i < _symbols.Count; i++)
                    {
                        var symbol = _symbols[i];
                        matrix[symbol] = new Dictionary<string, decimal>();
                        for (int j = 0; j < _symbols.Count; j++)
                        {
                            matrix[symbol][_symbols[j]] = (decimal)correlationMatrix[i, j];
                        }
                    }

                    return matrix;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета матрицы корреляции");
                    return _symbols.ToDictionary(s => s, s => _symbols.ToDictionary(s2 => s2, s2 => 0m));
                }
            }

            public TradingSignal GenerateCorrelationSignal(string symbol)
            {
                try
                {
                    var matrix = GetCorrelationMatrix();
                    var correlatedSymbols = matrix[symbol]
                        .Where(kv => kv.Value > 0.7m && kv.Key != symbol)
                        .ToList();

                    if (!correlatedSymbols.Any()) return null;

                    // Анализ направления коррелированных символов
                    var bullishCount = correlatedSymbols
                        .Count(kv => _priceHistory[kv.Key].Last() > _priceHistory[kv.Key][_priceHistory[kv.Key].Count - 2]);

                    var bearishCount = correlatedSymbols.Count - bullishCount;

                    return new TradingSignal
                    {
                        Symbol = symbol,
                        Direction = bullishCount > bearishCount ? TradeDirection.Long : TradeDirection.Short,
                        Confidence = (decimal)bullishCount / correlatedSymbols.Count,
                        Timestamp = DateTime.UtcNow
                    };
                }
                catch
                {
                    return null;
                }
            }

            private class CorrelationDataItem
            {
                public double BTCUSDT { get; set; }
                public double ETHUSDT { get; set; }
                public double BNBUSDT { get; set; }
            }
        }
        #endregion

        #region Data Models
        public enum TradeDirection { Long, Short }

        public class MarketDataPoint
        {
            public string Symbol { get; set; } = string.Empty;
            public KlineInterval TimeFrame { get; set; }
            public DateTime OpenTime { get; set; }
            public decimal Open { get; set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Close { get; set; }
            public decimal Volume { get; set; }
            public bool IsClosed { get; set; }
            public decimal RSI { get; set; }
            public decimal MACD { get; set; }
            public decimal Signal { get; set; }
            public decimal ATR { get; set; }
            public decimal SMA50 { get; set; }
            public decimal SMA200 { get; set; }
        }

        public class TradingSignal
        {
            public string Symbol { get; set; } = string.Empty;
            public TradeDirection Direction { get; set; }
            public decimal Confidence { get; set; }
            public DateTime Timestamp { get; set; }
            public KlineInterval TimeFrame { get; set; }
            public Dictionary<string, object> Features { get; set; } = new();
        }

        public class RiskMetrics
        {
            public decimal Volatility { get; set; }
            public decimal Liquidity { get; set; }
            public decimal PortfolioRisk { get; set; }
            public decimal PortfolioValue { get; set; }
            public decimal CVaR { get; set; }
            public Dictionary<string, Dictionary<string, decimal>> CorrelationMatrix { get; set; } = new();
            public List<OpenPosition> OpenPositions { get; set; } = new();
            public MarketTrend MarketTrend { get; set; }
        }

        public class OrderRequest
        {
            public string Symbol { get; set; } = string.Empty;
            public OrderSide Side { get; set; }
            public decimal Quantity { get; set; }
            public decimal Price { get; set; }
            public decimal StopLoss { get; set; }
            public decimal TakeProfit { get; set; }
            public bool UseTwap { get; set; }
            public KlineInterval TimeFrame { get; set; }
        }

        public class TradeResult
        {
            public bool Success { get; set; }
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = string.Empty;
            public decimal Quantity { get; set; }
            public decimal AveragePrice { get; set; }
            public decimal? ExitPrice { get; set; }
            public decimal StopLoss { get; set; }
            public decimal TakeProfit { get; set; }
            public DateTime EntryTime { get; set; }
            public DateTime? ExitTime { get; set; }
            public decimal? Profit { get; set; }
            public decimal Commission { get; set; }
            public decimal RiskPercent { get; set; }
            public bool IsSuccessful { get; set; }
            public decimal Slippage { get; set; }
            public string Error { get; set; }
        }

        public class OpenPosition
        {
            public string Symbol { get; set; } = string.Empty;
            public decimal Quantity { get; set; }
            public decimal EntryPrice { get; set; }
            public DateTime EntryTime { get; set; }
            public decimal StopLoss { get; set; }
            public decimal TakeProfit { get; set; }
            public TradeDirection Direction { get; set; }
            public decimal StopLossDistance { get; set; }
        }

        public class OrderBook
        {
            public List<OrderBookEntry> Bids { get; set; } = new();
            public List<OrderBookEntry> Asks { get; set; } = new();
            public DateTime Timestamp { get; set; }

            public decimal CalculateLiquidity(decimal currentPrice)
            {
                var minPrice = currentPrice * 0.98m;
                var maxPrice = currentPrice * 1.02m;

                var bidLiquidity = Bids
                    .Where(b => b.Price >= minPrice)
                    .Sum(b => b.Price * b.Quantity);

                var askLiquidity = Asks
                    .Where(a => a.Price <= maxPrice)
                    .Sum(a => a.Price * a.Quantity);

                return bidLiquidity + askLiquidity;
            }
        }

        public class OrderBookEntry
        {
            public decimal Price { get; set; }
            public decimal Quantity { get; set; }

            public OrderBookEntry(decimal price, decimal quantity)
            {
                Price = price;
                Quantity = quantity;
            }
        }

        public class PricePrediction
        {
            public float FuturePriceChange { get; set; }
        }

        public class BacktestResult
        {
            public bool Success { get; set; }
            public decimal SharpeRatio { get; set; }
            public decimal TotalReturn { get; set; }
            public decimal MaxDrawdown { get; set; }
            public decimal WinRate { get; set; }
            public string TimeFrame { get; set; }
            public List<TradeRecord> Trades { get; set; } = new();
        }

        public class TradeRecord
        {
            public string Symbol { get; set; } = string.Empty;
            public string Side { get; set; } = string.Empty;
            public decimal Quantity { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal? ExitPrice { get; set; }
            public decimal? Profit { get; set; }
            public decimal Commission { get; set; }
            public DateTime EntryTime { get; set; }
            public DateTime? ExitTime { get; set; }
            public decimal StopLoss { get; set; }
            public decimal TakeProfit { get; set; }
            public bool IsSuccessful { get; set; }
            public decimal RiskPercent { get; set; }
            public decimal Volatility { get; set; }
            public decimal Liquidity { get; set; }
            public decimal Slippage { get; set; }
            public string TimeFrame { get; set; }
            public string ExitReason { get; set; }
        }

        public class NewsEvent
        {
            public string Symbol { get; set; }
            public string Title { get; set; }
            public int ImpactLevel { get; set; } // 1-5, где 5 - максимальное влияние
            public DateTime PublishedAt { get; set; }
            public DateTime ExpiresAt { get; set; }
        }
        #endregion
    }
}