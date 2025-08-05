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
using Newtonsoft.Json;
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
        private const string PrimarySymbol = "BTCUSDT";
        private readonly List<string> _tradingSymbols = new() { "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "ADAUSDT", "XRPUSDT", "DOTUSDT", "DOGEUSDT", "AVAXUSDT", "MATICUSDT" };
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
        private int _maxParallelStrategies = 3; // Максимальное количество параллельных стратегий
        private decimal _dailyLossLimit = 0.1m; // Лимит дневных убытков (10%)

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
        private const decimal SandboxTestAmount = 100m;
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
        private readonly StrategyEvaluator _strategyEvaluator;
        #endregion

        #region State
        private volatile bool _isRunning;
        private DateTime _lastTradeTime;
        private decimal _currentBalance;
        private decimal _dailyStartingBalance;
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
        private readonly List<ActiveStrategy> _activeStrategies = new();
        private decimal _totalDailyLoss;
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
                        MaxParallelStrategies INTEGER NOT NULL DEFAULT 3,
                        DailyLossLimit REAL NOT NULL DEFAULT 0.1,
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
                    _maxParallelStrategies = reader.GetInt32(14);
                    _dailyLossLimit = reader.GetDecimal(15);
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
                maxConsecutiveLosses: _maxConsecutiveLosses,
                dailyLossLimit: _dailyLossLimit);

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
                symbols: _tradingSymbols,
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

            _strategyEvaluator = new StrategyEvaluator(
                _logger,
                _timeFrames,
                _tradingSymbols);
        }

        public async Task Run(bool enableLiveTrading = false)
        {
            try
            {
                _isRunning = true;
                _dailyStartingBalance = _isSandboxMode ? SandboxTestAmount : _initialBalance;
                _totalDailyLoss = 0;

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

                // Инициализация стратегий
                InitializeStrategies();

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

        private void InitializeStrategies()
        {
            // Основная стратегия (TA + ML)
            _activeStrategies.Add(new ActiveStrategy
            {
                Id = "TA_ML_Combo",
                Description = "Комбинация технического анализа и ML",
                IsActive = true,
                Weight = 0.6m,
                LastEvaluation = DateTime.UtcNow,
                PerformanceMetrics = new StrategyPerformance()
            });

            // Чисто ML стратегия
            _activeStrategies.Add(new ActiveStrategy
            {
                Id = "Pure_ML",
                Description = "Только ML предсказания",
                IsActive = true,
                Weight = 0.3m,
                LastEvaluation = DateTime.UtcNow,
                PerformanceMetrics = new StrategyPerformance()
            });

            // Стратегия следования тренду
            _activeStrategies.Add(new ActiveStrategy
            {
                Id = "Trend_Following",
                Description = "Следование тренду",
                IsActive = true,
                Weight = 0.1m,
                LastEvaluation = DateTime.UtcNow,
                PerformanceMetrics = new StrategyPerformance()
            });
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
                    _restClient.SpotApi.ExchangeData.GetExchangeInfoAsync(PrimarySymbol));

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
                PrimarySymbol,
                timeFrame,
                BacktestLookbackDays);
            results.Add(mainResult);

            // Бэктест в условиях высокой волатильности
            var volatileResult = await _backtester.RunBacktest(
                PrimarySymbol,
                timeFrame,
                lookbackDays: 30,
                filter: "HighVolatility");
            results.Add(volatileResult);

            // Бэктест в условиях низкой ликвидности
            var lowLiqResult = await _backtester.RunBacktest(
                PrimarySymbol,
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
                    // Проверка дневного лимита убытков
                    if (_totalDailyLoss >= _dailyStartingBalance * _dailyLossLimit)
                    {
                        await Notify($"⛔ Достигнут дневной лимит убытков: {_totalDailyLoss:F2} USDT");
                        await CloseAllPositions("Daily loss limit reached");
                        _circuitBreakerTriggered = true;
                        continue;
                    }

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

                    // Генерация сигналов для всех стратегий
                    var allSignals = new List<TradingSignal>();
                    foreach (var strategy in _activeStrategies.Where(s => s.IsActive))
                    {
                        var signals = await GenerateStrategySignals(strategy, marketData);
                        allSignals.AddRange(signals);
                    }

                    // Фильтрация сигналов
                    var filteredSignals = FilterSignals(allSignals, riskMetrics);

                    // Исполнение сделок с учетом весов стратегий
                    await ExecuteTrades(filteredSignals, riskMetrics);

                    // Обновление моделей ML
                    await _onlineModelTrainer.UpdateModels(marketData);

                    // Оценка эффективности стратегий
                    await EvaluateStrategies();

                    // Периодические задачи
                    await PerformPeriodicTasks();
                }
                catch (Exception ex)
                {
                    await HandleCriticalError(ex);
                }
            }
        }

        private async Task<List<TradingSignal>> GenerateStrategySignals(ActiveStrategy strategy, List<MarketDataPoint> marketData)
        {
            var signals = new List<TradingSignal>();

            switch (strategy.Id)
            {
                case "TA_ML_Combo":
                    // Комбинированная стратегия (TA + ML)
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

                        // Консенсус сигналов
                        if (taSignal.Direction == mlPrediction.Direction)
                        {
                            var confidence = (taSignal.Confidence + mlPrediction.Confidence) / 2;

                            signals.Add(new TradingSignal
                            {
                                StrategyId = strategy.Id,
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
                    break;

                case "Pure_ML":
                    // Чисто ML стратегия
                    foreach (var data in marketData.Where(d => d.TimeFrame == KlineInterval.OneHour).TakeLast(5))
                    {
                        var prediction = _onlineModelTrainer.Predict(data);
                        signals.Add(new TradingSignal
                        {
                            StrategyId = strategy.Id,
                            Symbol = data.Symbol,
                            Direction = prediction.Direction,
                            Confidence = prediction.Confidence,
                            Timestamp = DateTime.UtcNow,
                            TimeFrame = data.TimeFrame
                        });
                    }
                    break;

                case "Trend_Following":
                    // Стратегия следования тренду
                    var trendData = marketData
                        .Where(d => d.TimeFrame == KlineInterval.OneHour)
                        .OrderByDescending(d => d.OpenTime)
                        .Take(50)
                        .ToList();

                    if (trendData.Count >= 50)
                    {
                        var sma50 = trendData.Average(d => d.Close);
                        var sma200 = trendData.TakeLast(200).Average(d => d.Close);

                        foreach (var symbol in _tradingSymbols)
                        {
                            var currentPrice = _currentPrices.GetValueOrDefault(symbol);
                            if (currentPrice == 0) continue;

                            var direction = currentPrice > sma50 && sma50 > sma200
                                ? TradeDirection.Long
                                : TradeDirection.Short;

                            signals.Add(new TradingSignal
                            {
                                StrategyId = strategy.Id,
                                Symbol = symbol,
                                Direction = direction,
                                Confidence = 0.7m, // Фиксированная уверенность для трендовой стратегии
                                Timestamp = DateTime.UtcNow,
                                TimeFrame = KlineInterval.OneHour
                            });
                        }
                    }
                    break;
            }

            return signals;
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
                    _dailyStartingBalance = _currentBalance;
                    _totalDailyLoss = 0;
                }

                // Проверка задержек API
                await CheckApiLatency();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка при выполнении периодических задач");
            }
        }

        private async Task CheckApiLatency()
        {
            try
            {
                var latency = await _executionEngine.MeasureLatency();
                if (latency > MaxAllowedLatencyMs)
                {
                    await Notify($"⚠️ Высокая задержка API: {latency} мс");
                    // Приостанавливаем торговлю при высокой задержке
                    await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка проверки задержки API");
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
                        ExitReason = reason,
                        StrategyId = position.StrategyId
                    };

                    _portfolioManager.RecordTrade(trade);
                    _tradeHistory.Enqueue(trade);
                    await SaveTrade(trade);

                    // Обновляем метрики стратегии
                    if (!string.IsNullOrEmpty(position.StrategyId))
                    {
                        var strategy = _activeStrategies.FirstOrDefault(s => s.Id == position.StrategyId);
                        if (strategy != null)
                        {
                            strategy.PerformanceMetrics.TotalTrades++;
                            if (trade.Profit > 0)
                                strategy.PerformanceMetrics.ProfitableTrades++;
                            else
                                strategy.PerformanceMetrics.LosingTrades++;

                            strategy.PerformanceMetrics.TotalProfit += trade.Profit ?? 0;
                        }
                    }

                    // Обновляем дневные убытки
                    if (trade.Profit < 0)
                    {
                        _totalDailyLoss += Math.Abs(trade.Profit.Value);
                    }

                    _openPositions.Remove(position);
                    await Notify($"ℹ️ Позиция закрыта: {position.Symbol}. Причина: {reason}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Ошибка закрытия позиции {position.Symbol}");
            }
        }

        private async Task CloseAllPositions(string reason)
        {
            foreach (var position in _openPositions.ToList())
            {
                await ClosePosition(position, reason);
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

        private async Task EvaluateStrategies()
        {
            try
            {
                // Оцениваем стратегии раз в 4 часа
                if (DateTime.UtcNow.Hour % 4 == 0 && DateTime.UtcNow.Minute < 10)
                {
                    var evaluationResults = await _strategyEvaluator.EvaluateStrategies(
                        _activeStrategies,
                        _tradeHistory.ToList(),
                        _currentPrices);

                    // Обновляем веса стратегий на основе их эффективности
                    foreach (var result in evaluationResults)
                    {
                        var strategy = _activeStrategies.FirstOrDefault(s => s.Id == result.StrategyId);
                        if (strategy != null)
                        {
                            strategy.Weight = result.NewWeight;
                            strategy.IsActive = result.IsRecommended;
                            strategy.LastEvaluation = DateTime.UtcNow;
                            strategy.PerformanceMetrics = result.Performance;
                        }
                    }

                    await Notify("📊 Обновление стратегий:\n" +
                        string.Join("\n", evaluationResults.Select(r =>
                            $"{r.StrategyId}: Weight={r.NewWeight:P0}, WinRate={r.Performance.WinRate:P1}")));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка оценки стратегий");
            }
        }

        private async Task SendDailyReport()
        {
            try
            {
                var todayTrades = _tradeHistory
                    .Where(t => t.EntryTime.Date == DateTime.UtcNow.Date)
                    .ToList();

                var message = $"📊 Дневной отчет:\n" +
                               $"Сделок: {todayTrades.Count}\n" +
                               $"Прибыль: {todayTrades.Sum(t => t.Profit ?? 0):F2} USDT\n" +
                               $"Win Rate: {todayTrades.Count > 0 ? todayTrades.Count(t => t.Profit > 0) / (decimal)todayTrades.Count : 0m:P1}\n" +
                               $"Баланс: {_currentBalance:F2} USDT\n" +
                               $"Дневные убытки: {_totalDailyLoss:F2} USDT";

                await Notify(message);

                // Отчет по стратегиям
                if (_activeStrategies.Any())
                {
                    var strategiesReport = "\n📈 Эффективность стратегий:\n" +
                        string.Join("\n", _activeStrategies.Select(s =>
                            $"{s.Id} (Weight={s.Weight:P0}): Trades={s.PerformanceMetrics.TotalTrades}, WinRate={s.PerformanceMetrics.WinRate:P1}"));

                    await Notify(strategiesReport);
                }
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

            foreach (var symbol in _tradingSymbols)
            {
                foreach (var timeFrame in _timeFrames)
                {
                    var klinesResult = await _retryPolicy.ExecuteAsync(() =>
                        _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                            symbol,
                            timeFrame,
                            limit: 1000));

                    if (!klinesResult.Success)
                    {
                        _logger.LogWarning($"Не удалось получить данные для {symbol} {timeFrame}: {klinesResult.Error}");
                        continue;
                    }

                    var timeFrameData = klinesResult.Data.Select(k => new MarketDataPoint
                    {
                        Symbol = symbol,
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
            }

            return allData;
        }

        private async Task<RiskMetrics> CalculateRiskMetrics(List<MarketDataPoint> marketData)
        {
            var metrics = new RiskMetrics();

            foreach (var symbol in _tradingSymbols)
            {
                var symbolData = marketData.Where(d => d.Symbol == symbol).ToList();

                // Волатильность
                var volatility = _marketDataProcessor.CalculateVolatility(
                    symbolData.Where(d => d.TimeFrame == KlineInterval.OneHour).ToList(),
                    VolatilityLookbackPeriod);
                _volatilityCache[symbol] = volatility;

                // Ликвидность
                var liquidity = _orderBooks.TryGetValue(symbol, out var book) ?
                    book.CalculateLiquidity(_currentPrices.GetValueOrDefault(symbol, 0)) : 0m;
                _liquidityCache[symbol] = liquidity;
            }

            metrics.Volatility = _volatilityCache.GetValueOrDefault(PrimarySymbol, 0m);
            metrics.Liquidity = _liquidityCache.GetValueOrDefault(PrimarySymbol, 0m);
            metrics.PortfolioRisk = _riskEngine.CalculatePortfolioRisk(
                _openPositions,
                _currentPrices,
                _volatilityCache);
            metrics.CVaR = _riskEngine.CalculateCVaR(
                _tradeHistory.ToList(),
                VarConfidenceLevel);
            metrics.CorrelationMatrix = _correlationAnalyzer.GetCorrelationMatrix();
            metrics.PortfolioValue = _portfolioManager.CurrentBalance;
            metrics.OpenPositions = _openPositions;
            metrics.MarketTrend = _currentMarketTrend;

            return metrics;
        }

        private async Task ExecuteTrades(List<TradingSignal> signals, RiskMetrics riskMetrics)
        {
            if (signals.Count == 0 || _openPositions.Count >= _maxTradesPerDay)
                return;

            // Группируем сигналы по символам и стратегиям
            var groupedSignals = signals
                .GroupBy(s => new { s.Symbol, s.StrategyId })
                .Select(g => new
                {
                    g.Key.Symbol,
                    g.Key.StrategyId,
                    Direction = g.First().Direction, // Берем направление первого сигнала в группе
                    Confidence = g.Average(s => s.Confidence), // Усредненная уверенность
                    Weight = _activeStrategies.FirstOrDefault(s => s.Id == g.Key.StrategyId)?.Weight ?? 0.5m
                })
                .OrderByDescending(s => s.Confidence * s.Weight) // Сортируем по взвешенной уверенности
                .ToList();

            foreach (var signal in groupedSignals)
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

                    // Расчет размера позиции с учетом веса стратегии
                    var positionSize = _portfolioManager.CalculatePositionSize(
                        new TradingSignal
                        {
                            Symbol = signal.Symbol,
                            Direction = signal.Direction,
                            Confidence = signal.Confidence
                        },
                        _currentPrices[signal.Symbol],
                        riskMetrics) * signal.Weight;

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
                        StopLoss = CalculateStopLoss(signal.Direction, signal.Symbol, riskMetrics),
                        TakeProfit = CalculateTakeProfit(signal.Direction, signal.Symbol, riskMetrics),
                        UseTwap = positionSize > 0.1m * _currentPrices[signal.Symbol],
                        TimeFrame = KlineInterval.OneHour
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
                            TimeFrame = KlineInterval.OneHour.ToString(),
                            StrategyId = signal.StrategyId
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
                            StopLossDistance = Math.Abs(orderResult.AveragePrice - orderResult.StopLoss),
                            StrategyId = signal.StrategyId
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

        private decimal CalculateStopLoss(TradeDirection direction, string symbol, RiskMetrics riskMetrics)
        {
            var atr = _marketDataProcessor.GetLatestAtr(KlineInterval.OneHour);
            var stopDistance = atr * 1.5m * (1 + riskMetrics.Volatility);

            return direction == TradeDirection.Long
                ? _currentPrices[symbol] - stopDistance
                : _currentPrices[symbol] + stopDistance;
        }

        private decimal CalculateTakeProfit(TradeDirection direction, string symbol, RiskMetrics riskMetrics)
        {
            var entry = _currentPrices[symbol];
            var stopLoss = CalculateStopLoss(direction, symbol, riskMetrics);
            var risk = Math.Abs(entry - stopLoss);

            return direction == TradeDirection.Long
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
                    ExitReason TEXT,
                    StrategyId TEXT
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
                    ConsecutiveLosses INTEGER DEFAULT 0,
                    DailyStartingBalance REAL,
                    TotalDailyLoss REAL
                );

                CREATE TABLE IF NOT EXISTS NewsEvents (
                    Id INTEGER PRIMARY KEY,
                    Symbol TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    ImpactLevel INTEGER NOT NULL,
                    PublishedAt DATETIME NOT NULL,
                    ExpiresAt DATETIME NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Strategies (
                    Id TEXT PRIMARY KEY,
                    Description TEXT NOT NULL,
                    IsActive INTEGER NOT NULL,
                    Weight REAL NOT NULL,
                    LastEvaluation DATETIME NOT NULL,
                    TotalTrades INTEGER NOT NULL,
                    ProfitableTrades INTEGER NOT NULL,
                    LosingTrades INTEGER NOT NULL,
                    TotalProfit REAL NOT NULL
                );";

            await cmd.ExecuteNonQueryAsync();
        }

        private async Task LoadBotState()
        {
            using var cmd = _dbConnection.CreateCommand();
            cmd.CommandText = @"
                SELECT Balance, LastTradeTime, EmergencyStop, ConsecutiveLosses, 
                       DailyStartingBalance, TotalDailyLoss 
                FROM BotState LIMIT 1";

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                _currentBalance = reader.GetDecimal(0);
                _lastTradeTime = reader.GetDateTime(1);
                _circuitBreakerTriggered = reader.GetBoolean(2);
                _consecutiveLosses = reader.GetInt32(3);
                _dailyStartingBalance = reader.GetDecimal(4);
                _totalDailyLoss = reader.GetDecimal(5);
            }
            else
            {
                _currentBalance = _initialBalance;
                _lastTradeTime = DateTime.UtcNow;
                _dailyStartingBalance = _currentBalance;
                _totalDailyLoss = 0;

                cmd.CommandText = @"
                    INSERT INTO BotState 
                    (Balance, LastTradeTime, DailyStartingBalance, TotalDailyLoss) 
                    VALUES (@balance, @time, @dailyStart, @dailyLoss)";
                cmd.Parameters.AddWithValue("@balance", _currentBalance);
                cmd.Parameters.AddWithValue("@time", _lastTradeTime);
                cmd.Parameters.AddWithValue("@dailyStart", _dailyStartingBalance);
                cmd.Parameters.AddWithValue("@dailyLoss", _totalDailyLoss);
                await cmd.ExecuteNonQueryAsync();
            }

            // Загрузка стратегий из БД
            cmd.CommandText = "SELECT * FROM Strategies";
            using var strategiesReader = await cmd.ExecuteReaderAsync();
            while (await strategiesReader.ReadAsync())
            {
                _activeStrategies.Add(new ActiveStrategy
                {
                    Id = strategiesReader.GetString(0),
                    Description = strategiesReader.GetString(1),
                    IsActive = strategiesReader.GetBoolean(2),
                    Weight = strategiesReader.GetDecimal(3),
                    LastEvaluation = strategiesReader.GetDateTime(4),
                    PerformanceMetrics = new StrategyPerformance
                    {
                        TotalTrades = strategiesReader.GetInt32(5),
                        ProfitableTrades = strategiesReader.GetInt32(6),
                        LosingTrades = strategiesReader.GetInt32(7),
                        TotalProfit = strategiesReader.GetDecimal(8)
                    }
                });
            }
        }

        private async Task ConnectWebSockets()
        {
            // Подписка на свечные данные для всех символов и таймфреймов
            foreach (var symbol in _tradingSymbols)
            {
                foreach (var timeFrame in _timeFrames)
                {
                    var klineResult = await _socketClient.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(
                        symbol,
                        timeFrame,
                        data =>
                        {
                            var kline = data.Data;
                            _marketDataProcessor.ProcessKline(new MarketDataPoint
                            {
                                Symbol = symbol,
                                TimeFrame = timeFrame,
                                OpenTime = kline.Data.OpenTime,
                                Open = kline.Data.OpenPrice,
                                High = kline.Data.HighPrice,
                                Low = kline.Data.LowPrice,
                                Close = kline.Data.ClosePrice,
                                Volume = kline.Data.Volume,
                                IsClosed = kline.Data.Final
                            });

                            _currentPrices[symbol] = kline.Data.ClosePrice;
                        });

                    if (!klineResult.Success)
                        throw new Exception($"Ошибка подписки на свечи {symbol} {timeFrame}: {klineResult.Error}");
                }

                // Подписка на стакан цен
                var bookResult = await _socketClient.SpotApi.ExchangeData.SubscribeToOrderBookUpdatesAsync(
                    symbol,
                    1000,
                    data =>
                    {
                        _orderBooks[symbol] = new OrderBook
                        {
                            Bids = data.Data.Bids.Select(b => new OrderBookEntry(b.Price, b.Quantity)).ToList(),
                            Asks = data.Data.Asks.Select(a => new OrderBookEntry(a.Price, a.Quantity)).ToList(),
                            Timestamp = data.ReceiveTime
                        };
                    });

                if (!bookResult.Success)
                    throw new Exception($"Ошибка подписки на стакан {symbol}: {bookResult.Error}");
            }

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
                    Volatility, Liquidity, Slippage, TimeFrame, ExitReason, StrategyId
                ) VALUES (
                    @symbol, @side, @qty, @entry, @exit, @sl, @tp,
                    @entryTime, @exitTime, @profit, @commission, @success, @risk,
                    @volatility, @liquidity, @slippage, @timeFrame, @exitReason, @strategyId
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
            cmd.Parameters.AddWithValue("@strategyId", trade.StrategyId ?? (object)DBNull.Value);

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
                    ConsecutiveLosses = @losses,
                    DailyStartingBalance = @dailyStart,
                    TotalDailyLoss = @dailyLoss";

            cmd.Parameters.AddWithValue("@balance", _currentBalance);
            cmd.Parameters.AddWithValue("@time", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("@emergency", _circuitBreakerTriggered);
            cmd.Parameters.AddWithValue("@losses", _consecutiveLosses);
            cmd.Parameters.AddWithValue("@dailyStart", _dailyStartingBalance);
            cmd.Parameters.AddWithValue("@dailyLoss", _totalDailyLoss);

            await cmd.ExecuteNonQueryAsync();

            // Сохранение стратегий
            foreach (var strategy in _activeStrategies)
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO Strategies (
                        Id, Description, IsActive, Weight, LastEvaluation,
                        TotalTrades, ProfitableTrades, LosingTrades, TotalProfit
                    ) VALUES (
                        @id, @desc, @active, @weight, @eval,
                        @total, @profitable, @losing, @profit
                    )";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@id", strategy.Id);
                cmd.Parameters.AddWithValue("@desc", strategy.Description);
                cmd.Parameters.AddWithValue("@active", strategy.IsActive);
                cmd.Parameters.AddWithValue("@weight", strategy.Weight);
                cmd.Parameters.AddWithValue("@eval", strategy.LastEvaluation);
                cmd.Parameters.AddWithValue("@total", strategy.PerformanceMetrics.TotalTrades);
                cmd.Parameters.AddWithValue("@profitable", strategy.PerformanceMetrics.ProfitableTrades);
                cmd.Parameters.AddWithValue("@losing", strategy.PerformanceMetrics.LosingTrades);
                cmd.Parameters.AddWithValue("@profit", strategy.PerformanceMetrics.TotalProfit);

                await cmd.ExecuteNonQueryAsync();
            }
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

        private async Task CheckCircuitBreakerConditions()
        {
            // Проверяем, можно ли снять автостоп
            if (_consecutiveLosses < _circuitBreakerThreshold / 2)
            {
                _circuitBreakerTriggered = false;
                await Notify("🟢 Автостоп снят, торговля возобновлена");
            }
            else
            {
                await Task.Delay(TimeSpan.FromHours(1));
            }
        }

        private async Task<bool> CheckSystemHealth()
        {
            try
            {
                // Проверка баланса
                if (_currentBalance < _initialBalance * 0.5m)
                {
                    await Notify("⚠️ Низкий баланс! Торговля приостановлена");
                    return false;
                }

                // Проверка подключения
                var pingResult = await _restClient.SpotApi.ExchangeData.PingAsync();
                if (!pingResult.Success)
                {
                    await Notify("⚠️ Потеряно соединение с биржей");
                    return false;
                }

                // Проверка задержки
                var latency = await _executionEngine.MeasureLatency();
                if (latency > MaxAllowedLatencyMs)
                {
                    await Notify($"⚠️ Высокая задержка API: {latency} мс");
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task HandleCriticalError(Exception ex)
        {
            _logger.LogCritical(ex, "Критическая ошибка");
            await Notify($"‼️ Критическая ошибка: {ex.Message}\n{ex.StackTrace}");
            await Cleanup();
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

        /// <summary>
        /// Активная торговая стратегия с метриками производительности
        /// </summary>
        public class ActiveStrategy
        {
            /// <summary>
            /// Уникальный идентификатор стратегии
            /// </summary>
            public string Id { get; set; }

            /// <summary>
            /// Описание стратегии
            /// </summary>
            public string Description { get; set; }

            /// <summary>
            /// Активна ли стратегия
            /// </summary>
            public bool IsActive { get; set; } = true;

            /// <summary>
            /// Вес стратегии в распределении капитала (0-1)
            /// </summary>
            public decimal Weight { get; set; } = 0.5m;

            /// <summary>
            /// Время последней оценки стратегии
            /// </summary>
            public DateTime LastEvaluation { get; set; } = DateTime.UtcNow;

            /// <summary>
            /// Параметры стратегии
            /// </summary>
            public StrategyParameters Parameters { get; set; } = new StrategyParameters();

            /// <summary>
            /// Метрики производительности
            /// </summary>
            public StrategyPerformance PerformanceMetrics { get; set; } = new StrategyPerformance();

            /// <summary>
            /// История изменений веса стратегии
            /// </summary>
            public List<WeightAdjustment> WeightHistory { get; set; } = new List<WeightAdjustment>();

            /// <summary>
            /// Обновляет метрики стратегии на основе новой сделки
            /// </summary>
            public void UpdatePerformance(TradeRecord trade)
            {
                PerformanceMetrics.TotalTrades++;

                if (trade.Profit > 0)
                {
                    PerformanceMetrics.ProfitableTrades++;
                    PerformanceMetrics.TotalProfit += trade.Profit.Value;
                    PerformanceMetrics.ConsecutiveLosses = 0;
                    PerformanceMetrics.ConsecutiveWins++;
                }
                else
                {
                    PerformanceMetrics.LosingTrades++;
                    PerformanceMetrics.TotalLoss += Math.Abs(trade.Profit ?? 0);
                    PerformanceMetrics.ConsecutiveWins = 0;
                    PerformanceMetrics.ConsecutiveLosses++;
                }

                // Обновляем максимальные значения
                PerformanceMetrics.MaxProfit = Math.Max(PerformanceMetrics.MaxProfit, trade.Profit ?? 0);
                PerformanceMetrics.MaxLoss = Math.Min(PerformanceMetrics.MaxLoss, trade.Profit ?? 0);

                // Обновляем средние значения
                PerformanceMetrics.AvgProfit = PerformanceMetrics.TotalProfit / PerformanceMetrics.ProfitableTrades;
                PerformanceMetrics.AvgLoss = PerformanceMetrics.LosingTrades > 0
                    ? PerformanceMetrics.TotalLoss / PerformanceMetrics.LosingTrades
                    : 0;
            }

            /// <summary>
            /// Рассчитывает коэффициент Шарпа для стратегии
            /// </summary>
            public decimal CalculateSharpeRatio(decimal riskFreeRate = 0m)
            {
                if (PerformanceMetrics.TotalTrades == 0 || PerformanceMetrics.StdDevReturns == 0)
                    return 0m;

                return (PerformanceMetrics.AvgReturn - riskFreeRate) / PerformanceMetrics.StdDevReturns;
            }

            /// <summary>
            /// Рассчитывает коэффициент Сортино для стратегии
            /// </summary>
            public decimal CalculateSortinoRatio(decimal riskFreeRate = 0m)
            {
                if (PerformanceMetrics.TotalTrades == 0 || PerformanceMetrics.DownsideDeviation == 0)
                    return 0m;

                return (PerformanceMetrics.AvgReturn - riskFreeRate) / PerformanceMetrics.DownsideDeviation;
            }

            /// <summary>
            /// Корректирует вес стратегии на основе ее производительности
            /// </summary>
            public void AdjustWeight(decimal newWeight, string reason)
            {
                WeightHistory.Add(new WeightAdjustment
                {
                    OldWeight = Weight,
                    NewWeight = newWeight,
                    Timestamp = DateTime.UtcNow,
                    Reason = reason
                });

                Weight = newWeight;
            }

            /// <summary>
            /// Сбрасывает счетчики серий
            /// </summary>
            public void ResetStreaks()
            {
                PerformanceMetrics.ConsecutiveWins = 0;
                PerformanceMetrics.ConsecutiveLosses = 0;
            }
        }

        /// <summary>
        /// Параметры торговой стратегии
        /// </summary>
        public class StrategyParameters
        {
            /// <summary>
            /// Минимальный уровень уверенности для входа в сделку
            /// </summary>
            public decimal MinConfidence { get; set; } = 0.7m;

            /// <summary>
            /// Минимальное соотношение риск/прибыль
            /// </summary>
            public decimal MinRiskReward { get; set; } = 2m;

            /// <summary>
            /// Максимальный риск на сделку (% от капитала)
            /// </summary>
            public decimal MaxRiskPerTrade { get; set; } = 0.02m;

            /// <summary>
            /// Таймфрейм для анализа
            /// </summary>
            public KlineInterval TimeFrame { get; set; } = KlineInterval.OneHour;

            /// <summary>
            /// Использовать ли трейлинг-стоп
            /// </summary>
            public bool UseTrailingStop { get; set; } = true;

            /// <summary>
            /// Размер трейлинг-стопа (в ATR)
            /// </summary>
            public decimal TrailingStopAtrMultiplier { get; set; } = 1.5m;

            /// <summary>
            /// Максимальное количество сделок в день
            /// </summary>
            public int MaxDailyTrades { get; set; } = 5;

            /// <summary>
            /// Список инструментов для торговли
            /// </summary>
            public List<string> Symbols { get; set; } = new List<string>();
        }

        /// <summary>
        /// Метрики производительности стратегии
        /// </summary>
        public class StrategyPerformance
        {
            /// <summary>
            /// Общее количество сделок
            /// </summary>
            public int TotalTrades { get; set; }

            /// <summary>
            /// Количество прибыльных сделок
            /// </summary>
            public int ProfitableTrades { get; set; }

            /// <summary>
            /// Количество убыточных сделок
            /// </summary>
            public int LosingTrades { get; set; }

            /// <summary>
            /// Процент прибыльных сделок
            /// </summary>
            public decimal WinRate => TotalTrades > 0 ? ProfitableTrades / (decimal)TotalTrades : 0m;

            /// <summary>
            /// Общая прибыль
            /// </summary>
            public decimal TotalProfit { get; set; }

            /// <summary>
            /// Общий убыток
            /// </summary>
            public decimal TotalLoss { get; set; }

            /// <summary>
            /// Чистая прибыль
            /// </summary>
            public decimal NetProfit => TotalProfit - TotalLoss;

            /// <summary>
            /// Средняя прибыль
            /// </summary>
            public decimal AvgProfit => ProfitableTrades > 0 ? TotalProfit / ProfitableTrades : 0m;

            /// <summary>
            /// Средний убыток
            /// </summary>
            public decimal AvgLoss => LosingTrades > 0 ? TotalLoss / LosingTrades : 0m;

            /// <summary>
            /// Соотношение прибыль/убыток
            /// </summary>
            public decimal ProfitFactor => TotalLoss > 0 ? TotalProfit / TotalLoss : decimal.MaxValue;

            /// <summary>
            /// Максимальная прибыль в одной сделке
            /// </summary>
            public decimal MaxProfit { get; set; }

            /// <summary>
            /// Максимальный убыток в одной сделке
            /// </summary>
            public decimal MaxLoss { get; set; }

            /// <summary>
            /// Количество последовательных прибыльных сделок
            /// </summary>
            public int ConsecutiveWins { get; set; }

            /// <summary>
            /// Количество последовательных убыточных сделок
            /// </summary>
            public int ConsecutiveLosses { get; set; }

            /// <summary>
            /// Максимальная просадка
            /// </summary>
            public decimal MaxDrawdown { get; set; }

            /// <summary>
            /// Средняя доходность сделки
            /// </summary>
            public decimal AvgReturn { get; set; }

            /// <summary>
            /// Стандартное отклонение доходностей
            /// </summary>
            public decimal StdDevReturns { get; set; }

            /// <summary>
            /// Стандартное отклонение отрицательных доходностей
            /// </summary>
            public decimal DownsideDeviation { get; set; }

            /// <summary>
            /// Коэффициент Шарпа
            /// </summary>
            public decimal SharpeRatio { get; set; }

            /// <summary>
            /// Коэффициент Сортино
            /// </summary>
            public decimal SortinoRatio { get; set; }

            /// <summary>
            /// Обновляет статистику доходностей
            /// </summary>
            public void UpdateReturns(decimal returnValue)
            {
                // Здесь должна быть реализация обновления статистик
                // (скользящие средние, стандартные отклонения и т.д.)
            }
        }

        /// <summary>
        /// Запись об изменении веса стратегии
        /// </summary>
        public class WeightAdjustment
        {
            /// <summary>
            /// Старый вес
            /// </summary>
            public decimal OldWeight { get; set; }

            /// <summary>
            /// Новый вес
            /// </summary>
            public decimal NewWeight { get; set; }

            /// <summary>
            /// Время изменения
            /// </summary>
            public DateTime Timestamp { get; set; }

            /// <summary>
            /// Причина изменения
            /// </summary>
            public string Reason { get; set; }
        }

        /// <summary>
        /// Результат оценки стратегии
        /// </summary>
        public class StrategyEvaluationResult
        {
            /// <summary>
            /// Идентификатор стратегии
            /// </summary>
            public string StrategyId { get; set; }

            /// <summary>
            /// Рекомендуется ли использовать стратегию
            /// </summary>
            public bool IsRecommended { get; set; }

            /// <summary>
            /// Новый рекомендуемый вес
            /// </summary>
            public decimal NewWeight { get; set; }

            /// <summary>
            /// Оценка стратегии (0-100)
            /// </summary>
            public decimal Score { get; set; }

            /// <summary>
            /// Метрики производительности
            /// </summary>
            public StrategyPerformance Performance { get; set; } = new StrategyPerformance();

            /// <summary>
            /// Комментарии по оценке
            /// </summary>
            public string Comments { get; set; }
        }

        /// <summary>
        /// Типы стратегий
        /// </summary>
        public enum StrategyType
        {
            TrendFollowing,     // Трендовая
            MeanReversion,      // Возврат к среднему
            Breakout,           // Прорыв уровней
            Arbitrage,          // Арбитражная
            MarketMaking,       // Маркет-мейкинг
            MachineLearning,    // На основе ML
            Hybrid              // Гибридная
        }

        /// <summary>
        /// Статус стратегии
        /// </summary>
        public enum StrategyStatus
        {
            Active,             // Активна
            Paused,             // На паузе
            Disabled,           // Отключена
            Testing,            // В тестировании
            Optimizing          // В оптимизации
        }


        /// <summary>
        /// Класс для оценки и сравнения эффективности торговых стратегий
        /// </summary>
        public class StrategyEvaluator
        {
            private readonly ILogger _logger;
            private readonly List<KlineInterval> _timeFrames;
            private readonly List<string> _symbols;

            /// <summary>
            /// Конструктор инициализирует evaluator с необходимыми зависимостями
            /// </summary>
            /// <param name="logger">Логгер для записи событий</param>
            /// <param name="timeFrames">Список используемых таймфреймов</param>
            /// <param name="symbols">Список торговых символов</param>
            public StrategyEvaluator(ILogger logger, List<KlineInterval> timeFrames, List<string> symbols)
            {
                _logger = logger ?? throw new ArgumentNullException(nameof(logger));
                _timeFrames = timeFrames ?? throw new ArgumentNullException(nameof(timeFrames));
                _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
            }

            /// <summary>
            /// Основной метод оценки стратегий
            /// </summary>
            /// <param name="strategies">Список активных стратегий</param>
            /// <param name="tradeHistory">История сделок</param>
            /// <param name="currentPrices">Текущие цены</param>
            /// <returns>Список результатов оценки</returns>
            public async Task<List<StrategyEvaluationResult>> EvaluateStrategies(
                List<ActiveStrategy> strategies,
                List<TradeRecord> tradeHistory,
                ConcurrentDictionary<string, decimal> currentPrices)
            {
                var results = new List<StrategyEvaluationResult>();

                try
                {
                    // Параллельная оценка каждой стратегии
                    var evaluationTasks = strategies.Select(strategy =>
                        Task.Run(() => EvaluateSingleStrategy(strategy, tradeHistory)));

                    var evaluations = await Task.WhenAll(evaluationTasks);

                    // Нормализация весов на основе оценок
                    return NormalizeStrategyWeights(evaluations.ToList());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при оценке стратегий");
                    return strategies.Select(s => new StrategyEvaluationResult
                    {
                        StrategyId = s.Id,
                        IsRecommended = s.IsActive,
                        NewWeight = s.Weight,
                        Performance = s.PerformanceMetrics
                    }).ToList();
                }
            }

            /// <summary>
            /// Оценка одной конкретной стратегии
            /// </summary>
            private StrategyEvaluationResult EvaluateSingleStrategy(
                ActiveStrategy strategy,
                List<TradeRecord> allTrades)
            {
                var result = new StrategyEvaluationResult { StrategyId = strategy.Id };

                try
                {
                    // Фильтрация сделок по стратегии
                    var strategyTrades = allTrades
                        .Where(t => t.StrategyId == strategy.Id)
                        .ToList();

                    // Если нет сделок - оставляем вес без изменений
                    if (!strategyTrades.Any())
                    {
                        return new StrategyEvaluationResult
                        {
                            StrategyId = strategy.Id,
                            IsRecommended = false,
                            NewWeight = strategy.Weight * 0.8m, // Постепенно уменьшаем вес
                            Performance = strategy.PerformanceMetrics
                        };
                    }

                    // Расчет ключевых метрик
                    var recentTrades = strategyTrades
                        .Where(t => t.EntryTime >= DateTime.UtcNow.AddDays(-7))
                        .ToList();

                    var winRate = CalculateWinRate(recentTrades);
                    var profitFactor = CalculateProfitFactor(recentTrades);
                    var stabilityIndex = CalculateStabilityIndex(recentTrades);
                    var avgProfit = CalculateAverageProfit(recentTrades);

                    // Составная оценка стратегии (0-1)
                    var strategyScore = CalculateCompositeScore(
                        winRate,
                        profitFactor,
                        stabilityIndex,
                        avgProfit);

                    // Определение рекомендации
                    result.IsRecommended = strategyScore > 0.6m;
                    result.NewWeight = CalculateNewWeight(strategyScore, strategy.Weight);
                    result.Performance = new StrategyPerformance
                    {
                        TotalTrades = strategyTrades.Count,
                        ProfitableTrades = strategyTrades.Count(t => t.Profit > 0),
                        LosingTrades = strategyTrades.Count(t => t.Profit <= 0),
                        TotalProfit = strategyTrades.Sum(t => t.Profit ?? 0),
                        //WinRate = winRate,
                        //ProfitFactor = profitFactor,
                        //StabilityIndex = stabilityIndex
                    };

                    _logger.LogInformation(
                        $"Оценка стратегии {strategy.Id}: " +
                        $"Score={strategyScore:F2}, " +
                        $"WinRate={winRate:P1}, " +
                        $"ProfitFactor={profitFactor:F2}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка оценки стратегии {strategy.Id}");
                    result.IsRecommended = false;
                    result.NewWeight = strategy.Weight * 0.7m;
                }

                return result;
            }

            /// <summary>
            /// Расчет WinRate (доля прибыльных сделок)
            /// </summary>
            private decimal CalculateWinRate(List<TradeRecord> trades)
            {
                if (!trades.Any()) return 0m;
                return trades.Count(t => t.Profit > 0) / (decimal)trades.Count;
            }

            /// <summary>
            /// Расчет Profit Factor (отношение прибыли к убыткам)
            /// </summary>
            private decimal CalculateProfitFactor(List<TradeRecord> trades)
            {
                var grossProfit = trades.Where(t => t.Profit > 0).Sum(t => t.Profit) ?? 0m;
                var grossLoss = Math.Abs(trades.Where(t => t.Profit < 0).Sum(t => t.Profit) ?? 0m);

                return grossLoss > 0 ? grossProfit / grossLoss : grossProfit > 0 ? 10m : 0m;
            }

            /// <summary>
            /// Индекс стабильности (меньше = более стабильная доходность)
            /// </summary>
            private decimal CalculateStabilityIndex(List<TradeRecord> trades)
            {
                if (trades.Count < 5) return 1m;

                var profits = trades
                    .Select(t => t.Profit ?? 0)
                    .ToList();

                var avg = profits.Average();
                var stdDev = (decimal)Math.Sqrt(profits.Average(p => Math.Pow((double)(p - avg), 2)));

                return stdDev / (Math.Abs(avg) > 0 ? Math.Abs(avg) : 1m);
            }

            /// <summary>
            /// Средняя прибыль на сделку (в % от размера позиции)
            /// </summary>
            private decimal CalculateAverageProfit(List<TradeRecord> trades)
            {
                if (!trades.Any()) return 0m;

                return trades.Average(t =>
                {
                    var size = t.EntryPrice * t.Quantity;
                    return size > 0 ? (t.Profit ?? 0) / size : 0m;
                });
            }

            /// <summary>
            /// Композитный скор стратегии (0-1)
            /// </summary>
            private decimal CalculateCompositeScore(
                decimal winRate,
                decimal profitFactor,
                decimal stabilityIndex,
                decimal avgProfit)
            {
                // Весовые коэффициенты
                const decimal winRateWeight = 0.4m;
                const decimal profitFactorWeight = 0.3m;
                const decimal stabilityWeight = 0.2m;
                const decimal avgProfitWeight = 0.1m;

                // Нормализация значений
                var normalizedWinRate = winRate;
                var normalizedProfitFactor = Math.Min(profitFactor / 5m, 1m);
                var normalizedStability = 1m - Math.Min(stabilityIndex, 1m);
                var normalizedAvgProfit = Math.Min(Math.Abs(avgProfit) * 100m, 1m);

                // Расчет общего скора
                return winRateWeight * normalizedWinRate +
                       profitFactorWeight * normalizedProfitFactor +
                       stabilityWeight * normalizedStability +
                       avgProfitWeight * normalizedAvgProfit;
            }

            /// <summary>
            /// Расчет нового веса стратегии
            /// </summary>
            private decimal CalculateNewWeight(decimal strategyScore, decimal currentWeight)
            {
                // Максимальное изменение веса за одну оценку (±30%)
                const decimal maxChange = 0.3m;

                // Целевой вес на основе скора
                var targetWeight = strategyScore * 0.9m; // 0-0.9

                // Плавное изменение веса
                var newWeight = currentWeight;

                if (targetWeight > currentWeight)
                {
                    newWeight = Math.Min(
                        currentWeight * (1 + maxChange),
                        targetWeight);
                }
                else
                {
                    newWeight = Math.Max(
                        currentWeight * (1 - maxChange),
                        targetWeight);
                }

                // Ограничения минимального и максимального веса
                return Math.Clamp(newWeight, 0.05m, 0.8m);
            }

            /// <summary>
            /// Нормализация весов стратегий (сумма = 1)
            /// </summary>
            private List<StrategyEvaluationResult> NormalizeStrategyWeights(
                List<StrategyEvaluationResult> results)
            {
                if (!results.Any()) return results;

                var totalWeight = results.Sum(r => r.NewWeight);

                if (totalWeight <= 0)
                {
                    // Равномерное распределение при ошибке
                    var defaultWeight = 1m / results.Count;
                    results.ForEach(r => r.NewWeight = defaultWeight);
                    return results;
                }

                // Нормализация
                results.ForEach(r =>
                    r.NewWeight = r.NewWeight / totalWeight);

                return results;
            }

            /// <summary>
            /// Дополнительная оценка корреляции между стратегиями
            /// </summary>
            public Dictionary<string, decimal> CalculateStrategyCorrelations(
                List<TradeRecord> tradeHistory)
            {
                var correlationMatrix = new Dictionary<string, decimal>();
                var strategyIds = tradeHistory
                    .Select(t => t.StrategyId)
                    .Distinct()
                    .ToList();

                foreach (var strategy1 in strategyIds)
                {
                    foreach (var strategy2 in strategyIds)
                    {
                        if (strategy1 == strategy2)
                        {
                            correlationMatrix[$"{strategy1}_{strategy2}"] = 1m;
                            continue;
                        }

                        var trades1 = tradeHistory
                            .Where(t => t.StrategyId == strategy1)
                            .OrderBy(t => t.EntryTime)
                            .ToList();

                        var trades2 = tradeHistory
                            .Where(t => t.StrategyId == strategy2)
                            .OrderBy(t => t.EntryTime)
                            .ToList();

                        correlationMatrix[$"{strategy1}_{strategy2}"] =
                            CalculateReturnsCorrelation(trades1, trades2);
                    }
                }

                return correlationMatrix;
            }

            /// <summary>
            /// Расчет корреляции доходностей между стратегиями
            /// </summary>
            private decimal CalculateReturnsCorrelation(
                List<TradeRecord> trades1,
                List<TradeRecord> trades2)
            {
                try
                {
                    if (!trades1.Any() || !trades2.Any()) return 0m;

                    // Создание совместной временной шкалы
                    var allDates = trades1.Select(t => t.EntryTime.Date)
                        .Union(trades2.Select(t => t.EntryTime.Date))
                        .Distinct()
                        .OrderBy(d => d)
                        .ToList();

                    // Сбор дневных доходностей
                    var returns1 = new List<decimal>();
                    var returns2 = new List<decimal>();

                    for (int i = 1; i < allDates.Count; i++)
                    {
                        var date = allDates[i];
                        var prevDate = allDates[i - 1];

                        var profit1 = trades1
                            .Where(t => t.EntryTime.Date > prevDate && t.EntryTime.Date <= date)
                            .Sum(t => t.Profit ?? 0);

                        var profit2 = trades2
                            .Where(t => t.EntryTime.Date > prevDate && t.EntryTime.Date <= date)
                            .Sum(t => t.Profit ?? 0);

                        returns1.Add(profit1);
                        returns2.Add(profit2);
                    }

                    // Расчет корреляции Пирсона
                    return CalculatePearsonCorrelation(returns1, returns2);
                }
                catch
                {
                    return 0m;
                }
            }

            /// <summary>
            /// Расчет коэффициента корреляции Пирсона
            /// </summary>
            private decimal CalculatePearsonCorrelation(List<decimal> x, List<decimal> y)
            {
                if (x.Count != y.Count || x.Count < 2) return 0m;

                var n = x.Count;
                decimal sumX = 0, sumY = 0, sumXY = 0;
                decimal sumX2 = 0, sumY2 = 0;

                for (int i = 0; i < n; i++)
                {
                    sumX += x[i];
                    sumY += y[i];
                    sumXY += x[i] * y[i];
                    sumX2 += x[i] * x[i];
                    sumY2 += y[i] * y[i];
                }

                var numerator = sumXY - (sumX * sumY / n);
                var denominator = (decimal)Math.Sqrt(
                    (double)((sumX2 - (sumX * sumX / n)) *
                    (double)((sumY2 - (sumY * sumY / n))));

                return denominator != 0 ? numerator / denominator : 0m;
            }
        }



        /// <summary>
        /// Улучшенный движок управления рисками с расширенными функциями
        /// </summary>
        public class EnhancedRiskEngine
        {
            // Конфигурационные параметры
            private readonly decimal _maxDrawdown;        // Максимально допустимая просадка (например, 25%)
            private readonly decimal _maxRiskPerTrade;    // Максимальный риск на сделку (например, 2%)
            private readonly decimal _kellyFraction;      // Доля критерия Келли (например, 0.5 для половинного Келли)
            private readonly decimal _maxLeverage;       // Максимальное плечо
            private readonly decimal _varConfidenceLevel; // Уровень доверия для VaR (например, 0.95)
            private readonly decimal _maxVolatility;      // Максимально допустимая волатильность
            private readonly decimal _minLiquidity;      // Минимальная ликвидность
            private readonly int _maxConsecutiveLosses;  // Макс число убыточных сделок подряд
            private readonly decimal _dailyLossLimit;    // Дневной лимит убытков (например, 10%)
            private readonly ILogger _logger;           // Логгер

            /// <summary>
            /// Конструктор движка рисков
            /// </summary>
            public EnhancedRiskEngine(
                decimal maxDrawdown,
                decimal maxRiskPerTrade,
                decimal kellyFraction,
                decimal maxLeverage,
                decimal varConfidenceLevel,
                decimal maxVolatility,
                decimal minLiquidity,
                int maxConsecutiveLosses,
                decimal dailyLossLimit,
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
                _dailyLossLimit = dailyLossLimit;
                _logger = logger;
            }

            /// <summary>
            /// Проверяет, разрешена ли сделка согласно текущим рискам
            /// </summary>
            /// <param name="signal">Торговый сигнал</param>
            /// <param name="riskMetrics">Текущие метрики риска</param>
            /// <param name="marketTrend">Текущий рыночный тренд</param>
            /// <returns>True, если сделка разрешена</returns>
            public bool IsTradeAllowed(TradingSignal signal, RiskMetrics riskMetrics, MarketTrend marketTrend)
            {
                try
                {
                    // 1. Проверка волатильности
                    if (riskMetrics.Volatility > _maxVolatility)
                    {
                        _logger?.LogInformation($"Торговля заблокирована: волатильность {riskMetrics.Volatility:P2} > {_maxVolatility:P2}");
                        return false;
                    }

                    // 2. Проверка ликвидности
                    if (riskMetrics.Liquidity < _minLiquidity)
                    {
                        _logger?.LogInformation($"Торговля заблокирована: ликвидность {riskMetrics.Liquidity:F2} < {_minLiquidity:F2}");
                        return false;
                    }

                    // 3. Проверка риска портфеля
                    if (riskMetrics.PortfolioRisk > _maxRiskPerTrade)
                    {
                        _logger?.LogInformation($"Торговля заблокирована: риск портфеля {riskMetrics.PortfolioRisk:P2} > {_maxRiskPerTrade:P2}");
                        return false;
                    }

                    // 4. Проверка CVaR (Conditional Value at Risk)
                    if (riskMetrics.CVaR > _maxDrawdown)
                    {
                        _logger?.LogInformation($"Торговля заблокирована: CVaR {riskMetrics.CVaR:P2} > {_maxDrawdown:P2}");
                        return false;
                    }

                    // 5. Проверка на соответствие тренду
                    if ((signal.Direction == TradeDirection.Long && marketTrend == MarketTrend.Bearish) ||
                        (signal.Direction == TradeDirection.Short && marketTrend == MarketTrend.Bullish))
                    {
                        _logger?.LogInformation($"Торговля заблокирована: сигнал против тренда");
                        return false;
                    }

                    // 6. Проверка корреляции с открытыми позициями
                    foreach (var pos in riskMetrics.OpenPositions)
                    {
                        var correlation = riskMetrics.CorrelationMatrix[signal.Symbol][pos.Symbol];
                        if (correlation > 0.8m)
                        {
                            _logger?.LogInformation($"Торговля заблокирована: высокая корреляция ({correlation:P2}) с {pos.Symbol}");
                            return false;
                        }
                    }

                    // 7. Проверка дневного лимита убытков
                    if (riskMetrics.DailyProfitLoss < -_dailyLossLimit * riskMetrics.PortfolioValue)
                    {
                        _logger?.LogInformation($"Торговля заблокирована: достигнут дневной лимит убытков");
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка проверки допустимости сделки");
                    return false;
                }
            }

            /// <summary>
            /// Рассчитывает оптимальный размер позиции
            /// </summary>
            /// <param name="signal">Торговый сигнал</param>
            /// <param name="currentPrice">Текущая цена</param>
            /// <param name="riskMetrics">Метрики риска</param>
            /// <returns>Размер позиции в базовой валюте</returns>
            public decimal CalculatePositionSize(
                TradingSignal signal,
                decimal currentPrice,
                RiskMetrics riskMetrics)
            {
                try
                {
                    // Базовые параметры стратегии (должны настраиваться на основе бэктеста)
                    const decimal winRate = 0.55m;     // Процент прибыльных сделок
                    const decimal avgWin = 0.03m;      // Средний выигрыш (3%)
                    const decimal avgLoss = 0.015m;    // Средний проигрыш (1.5%)

                    // 1. Расчет критерия Келли
                    var kelly = winRate - ((1 - winRate) / (avgWin / avgLoss));

                    // 2. Учет текущего риска портфеля
                    var fraction = kelly * _kellyFraction * (1 - riskMetrics.PortfolioRisk);

                    // 3. Минимальный и максимальный размер позиции
                    var minSize = 0.001m; // Минимальный допустимый размер
                    var maxSize = riskMetrics.Liquidity * 0.1m / currentPrice; // Не более 10% от ликвидности

                    // 4. Базовый расчет размера позиции
                    var size = (riskMetrics.PortfolioValue * fraction) / currentPrice;

                    // 5. Корректировки:
                    // - Учет волатильности (уменьшаем размер при высокой волатильности)
                    size *= 1 - (riskMetrics.Volatility / _maxVolatility);

                    // - Учет корреляции с текущими позициями
                    size *= GetCorrelationAdjustment(signal.Symbol, riskMetrics);

                    // - Учет текущего дневного PnL
                    size *= 1 - (riskMetrics.DailyProfitLoss / riskMetrics.PortfolioValue);

                    // 6. Ограничение размера
                    return Math.Clamp(size, minSize, maxSize);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета размера позиции");
                    return 0m;
                }
            }

            /// <summary>
            /// Рассчитывает поправочный коэффициент на корреляцию
            /// </summary>
            private decimal GetCorrelationAdjustment(string symbol, RiskMetrics metrics)
            {
                decimal maxCorrelation = 0;

                // Находим максимальную корреляцию с текущими позициями
                foreach (var pos in metrics.OpenPositions)
                {
                    var correlation = metrics.CorrelationMatrix[symbol][pos.Symbol];
                    if (correlation > maxCorrelation)
                        maxCorrelation = correlation;
                }

                // Чем выше корреляция, тем сильнее уменьшаем размер
                return 1 - (maxCorrelation * 0.5m);
            }

            /// <summary>
            /// Рассчитывает риск текущего портфеля
            /// </summary>
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

                        var positionValue = position.Quantity * price;
                        var volatility = volatilities.GetValueOrDefault(position.Symbol, 0m);

                        // Риск позиции = стоимость * волатильность
                        totalRisk += positionValue * volatility;
                        totalValue += positionValue;
                    }

                    // Общий риск портфеля = суммарный риск / суммарную стоимость
                    return totalValue > 0 ? totalRisk / totalValue : 0m;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета риска портфеля");
                    return 1m; // В случае ошибки возвращаем максимальный риск
                }
            }

            /// <summary>
            /// Рассчитывает Conditional Value at Risk (CVaR)
            /// </summary>
            public decimal CalculateCVaR(List<TradeRecord> trades, decimal confidenceLevel)
            {
                if (trades.Count < 50) return 0m;

                try
                {
                    // 1. Отбираем только убыточные сделки и сортируем по убыванию убытка
                    var losses = trades
                        .Where(t => t.Profit < 0)
                        .Select(t => -t.Profit.Value)
                        .OrderByDescending(l => l)
                        .ToList();

                    if (!losses.Any()) return 0m;

                    // 2. Определяем индекс для заданного уровня доверия
                    var index = (int)(losses.Count * (1 - confidenceLevel));

                    // 3. Возвращаем средний убыток в хвосте распределения
                    return index > 0 ? (decimal)losses.Take(index).Average() : 0m;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета CVaR");
                    return 0m;
                }
            }

            /// <summary>
            /// Проводит стресс-тест портфеля
            /// </summary>
            public StressTestResult RunStressTest(
                List<OpenPosition> positions,
                MarketScenario scenario,
                ConcurrentDictionary<string, decimal> currentPrices)
            {
                var result = new StressTestResult();

                try
                {
                    // 1. Рассчитываем текущую стоимость портфеля
                    decimal portfolioValue = positions.Sum(p =>
                        currentPrices.GetValueOrDefault(p.Symbol, 0m) * p.Quantity);

                    decimal stressedValue = 0;

                    // 2. Применяем сценарий стресс-теста к каждой позиции
                    foreach (var position in positions)
                    {
                        var price = currentPrices.GetValueOrDefault(position.Symbol, 0m);
                        if (price == 0m) continue;

                        decimal newPrice = scenario.Type switch
                        {
                            ScenarioType.MarketCrash => price * (1 - scenario.Severity),
                            ScenarioType.VolatilitySpike => price * (1 + (scenario.Severity *
                                (position.Direction == TradeDirection.Long ? -1 : 1))),
                            _ => price
                        };

                        stressedValue += newPrice * position.Quantity;
                    }

                    // 3. Рассчитываем максимальную просадку
                    result.MaxDrawdown = (portfolioValue - stressedValue) / portfolioValue;
                    result.IsAcceptable = result.MaxDrawdown < _maxDrawdown;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка стресс-теста");
                    result.IsAcceptable = false;
                }

                return result;
            }

            /// <summary>
            /// Определяет, требуется ли хеджирование позиции
            /// </summary>
            public bool RequiresHedging(
                OpenPosition position,
                RiskMetrics metrics,
                ConcurrentDictionary<string, decimal> currentPrices)
            {
                try
                {
                    // Если позиция уже хеджирована, ничего не делаем
                    if (position.IsHedged) return false;

                    var price = currentPrices.GetValueOrDefault(position.Symbol, 0m);
                    if (price == 0m) return false;

                    // Определяем условия для хеджирования:
                    // 1. Позиция крупная относительно портфеля (>20%)
                    bool isLargePosition = position.Quantity * price > 0.2m * metrics.PortfolioValue;

                    // 2. Высокая волатильность (>80% от максимально допустимой)
                    bool isHighVolatility = metrics.Volatility > _maxVolatility * 0.8m;

                    // 3. Есть важные новости (уровень воздействия >= 3)
                    bool hasImportantNews = metrics.NewsImpactLevel >= 3;

                    // Хеджируем если позиция крупная И (высокая волатильность ИЛИ важные новости)
                    return isLargePosition && (isHighVolatility || hasImportantNews);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка проверки хеджирования");
                    return false;
                }
            }
        }

        /// <summary>
        /// Результат стресс-теста
        /// </summary>
        public class StressTestResult
        {
            public decimal MaxDrawdown { get; set; }  // Максимальная просадка (0-1)
            public bool IsAcceptable { get; set; }    // Соответствует ли риск-политике
        }

        /// <summary>
        /// Типы сценариев для стресс-теста
        /// </summary>
        public enum ScenarioType
        {
            MarketCrash,      // Обвал рынка
            VolatilitySpike,  // Резкий рост волатильности
            LiquidityCrisis   // Кризис ликвидности
        }

        /// <summary>
        /// Сценарий для стресс-теста
        /// </summary>
        public class MarketScenario
        {
            public ScenarioType Type { get; set; }     // Тип сценария
            public decimal Severity { get; set; }      // Степень тяжести (0-1)
            public string[] AffectedSymbols { get; set; } // Затронутые инструменты
        }


        /// <summary>
        /// Улучшенный движок исполнения ордеров с поддержкой:
        /// - VWAP/TWAP стратегий
        /// - Частичного исполнения
        /// - Контроля ликвидности
        /// - Хеджирования позиций
        /// </summary>
        public class EnhancedExecutionEngine
        {
            private readonly IBinanceRestClient _restClient;
            private readonly IBinanceSocketClient _socketClient;
            private readonly AsyncRetryPolicy _retryPolicy;
            private readonly ILogger _logger;
            private readonly bool _isSandbox;

            // Трекер активных ордеров для мониторинга исполнения
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

            /// <summary>
            /// Основной метод исполнения ордера с интеллектуальным выбором стратегии
            /// </summary>
            public async Task<TradeResult> ExecuteOrder(OrderRequest request)
            {
                try
                {
                    // 1. Проверка ликвидности перед исполнением
                    var liquidityCheck = await CheckLiquidity(request.Symbol, request.Quantity, request.Price);
                    if (!liquidityCheck.IsSufficient)
                    {
                        _logger.LogWarning($"Недостаточная ликвидность для {request.Symbol}. Доступно: {liquidityCheck.AvailableLiquidity}, требуется: {request.Quantity}");

                        // 2. Попытка разбить крупный ордер на части
                        if (request.Quantity > liquidityCheck.MinimalChunkSize)
                        {
                            return await ExecutePartialOrder(request, liquidityCheck);
                        }

                        return new TradeResult { Success = false, Error = "Недостаточная ликвидность" };
                    }

                    // 3. Выбор стратегии исполнения:
                    // - VWAP для крупных ордеров (>10% от дневного объема)
                    // - Мгновенное исполнение для мелких
                    if (request.UseVwap && request.Quantity > 0.1m * request.Price)
                    {
                        return await ExecuteVwapOrder(request);
                    }

                    return await ExecuteInstantOrder(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Ошибка исполнения ордера {request.Symbol}");
                    return new TradeResult { Success = false, Error = ex.Message };
                }
            }

            /// <summary>
            /// VWAP стратегия исполнения (объем-взвешенная средняя цена)
            /// </summary>
            private async Task<TradeResult> ExecuteVwapOrder(OrderRequest request)
            {
                const int chunks = 5; // Количество частей для разбивки
                decimal chunkSize = request.Quantity / chunks;
                decimal remaining = request.Quantity;
                decimal totalFilled = 0;
                decimal totalCost = 0;
                decimal totalCommission = 0;
                decimal totalSlippage = 0;

                for (int i = 0; i < chunks && remaining > 0; i++)
                {
                    // Интервал между частями для имитации естественного потока
                    await Task.Delay(TimeSpan.FromSeconds(30));

                    var orderResult = await _retryPolicy.ExecuteAsync(async () =>
                    {
                        var order = new BinancePlaceOrderRequest
                        {
                            Symbol = request.Symbol,
                            Side = request.Side,
                            Type = SpotOrderType.Market, // Рыночный ордер для части
                            Quantity = Math.Min(chunkSize, remaining),
                            NewClientOrderId = $"vwap_{i}_{DateTime.UtcNow.Ticks}"
                        };

                        return await _restClient.SpotApi.Trading.PlaceOrderAsync(order);
                    });

                    if (!orderResult.Success)
                    {
                        _logger.LogError($"VWAP часть {i} не исполнена: {orderResult.Error}");
                        continue;
                    }

                    // Агрегирование результатов частичного исполнения
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
                    return new TradeResult { Success = false, Error = "Не удалось исполнить ни одной части VWAP" };
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

            /// <summary>
            /// Мгновенное исполнение лимитного ордера
            /// </summary>
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

                // Ожидание полного исполнения ордера
                var executionResult = await WaitForOrderExecution(request.Symbol, orderResult.Data.Id);
                return MapToTradeResult(request, executionResult);
            }

            /// <summary>
            /// Ожидание исполнения ордера с таймаутом
            /// </summary>
            private async Task<BinanceOrder> WaitForOrderExecution(string symbol, long orderId)
            {
                var timeout = DateTime.UtcNow.Add(TimeSpan.FromSeconds(30));
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

                        await Task.Delay(1000); // Пауза между проверками
                    }

                    // Отмена по таймауту
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

            /// <summary>
            /// Проверка достаточности ликвидности для ордера
            /// </summary>
            private async Task<LiquidityCheckResult> CheckLiquidity(string symbol, decimal quantity, decimal price)
            {
                var orderBook = await _retryPolicy.ExecuteAsync(() =>
                    _restClient.SpotApi.ExchangeData.GetOrderBookAsync(symbol, 50));

                if (!orderBook.Success)
                    return new LiquidityCheckResult { IsSufficient = false };

                decimal available = 0;
                decimal minChunk = decimal.MaxValue;

                // Анализ стакана на доступный объем
                if (orderBook.Data.Asks.Any())
                {
                    available = orderBook.Data.Asks
                        .Where(a => a.Price <= price * 1.01m) // +1% от текущей цены
                        .Sum(a => a.Quantity * a.Price);

                    minChunk = orderBook.Data.Asks.Min(a => a.Quantity);
                }

                return new LiquidityCheckResult
                {
                    IsSufficient = available >= quantity * price,
                    AvailableLiquidity = available,
                    MinimalChunkSize = minChunk
                };
            }

            /// <summary>
            /// Частичное исполнение крупного ордера
            /// </summary>
            private async Task<TradeResult> ExecutePartialOrder(OrderRequest request, LiquidityCheckResult liquidity)
            {
                decimal remaining = request.Quantity;
                decimal totalFilled = 0;
                decimal totalCost = 0;
                decimal totalCommission = 0;
                decimal totalSlippage = 0;

                while (remaining > 0)
                {
                    // Размер части (90% от минимального доступного объема)
                    decimal chunk = Math.Min(remaining, liquidity.MinimalChunkSize * 0.9m);
                    var partialRequest = new OrderRequest
                    {
                        Symbol = request.Symbol,
                        Side = request.Side,
                        Quantity = chunk,
                        Price = request.Price,
                        StopLoss = request.StopLoss,
                        TakeProfit = request.TakeProfit
                    };

                    var result = await ExecuteInstantOrder(partialRequest);
                    if (!result.Success)
                        break;

                    // Агрегирование результатов
                    totalFilled += result.Quantity;
                    totalCost += result.Quantity * result.AveragePrice;
                    totalCommission += result.Commission;
                    totalSlippage += result.Slippage;
                    remaining -= result.Quantity;

                    await Task.Delay(1000); // Пауза между частичными исполнениями
                }

                return new TradeResult
                {
                    Success = totalFilled > 0,
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

            /// <summary>
            /// Обработка обновлений ордеров через WebSocket
            /// </summary>
            public void ProcessOrderUpdate(BinanceStreamOrderUpdate update)
            {
                if (_activeOrders.TryGetValue(update.Data.Id, out var order))
                {
                    _activeOrders[update.Data.Id] = update.Data;
                    if (update.Data.Status.IsFinal())
                    {
                        _activeOrders.TryRemove(update.Data.Id, out _);
                    }
                }
            }

            /// <summary>
            /// Измерение задержки API (в миллисекундах)
            /// </summary>
            public async Task<decimal> MeasureLatency()
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await _restClient.SpotApi.ExchangeData.GetServerTimeAsync();
                return sw.ElapsedMilliseconds;
            }

            /// <summary>
            /// Преобразование BinanceOrder в TradeResult
            /// </summary>
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

            /// <summary>
            /// Результат проверки ликвидности
            /// </summary>
            private struct LiquidityCheckResult
            {
                public bool IsSufficient { get; set; }
                public decimal AvailableLiquidity { get; set; }
                public decimal MinimalChunkSize { get; set; }
            }
        }









        /// <summary></summary>
        public class MultiTimeFrameMarketDataProcessor
        {
            private readonly Dictionary<KlineInterval, List<MarketDataPoint>> _dataCache = new();
            private readonly object _cacheLock = new();
            private readonly List<KlineInterval> _timeFrames;
            private readonly Dictionary<string, OrderBook> _lastOrderBooks = new();

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

                    if (_dataCache[data.TimeFrame].Count > 5000)
                    {
                        _dataCache[data.TimeFrame].RemoveRange(0, _dataCache[data.TimeFrame].Count - 5000);
                    }
                }

                CalculateIndicators(data);
            }

            public void ProcessOrderBook(OrderBook book)
            {
                _lastOrderBooks[book.Symbol] = book;
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
                    double[] volumes = timeFrameData.Select(d => (double)d.Volume).ToArray();

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

                    // SMA
                    double[] sma50 = new double[closes.Length];
                    Core.Sma(closes, 0, closes.Length - 1, sma50, out _, out _, 50);
                    data.SMA50 = (decimal)sma50.Last();

                    double[] sma200 = new double[closes.Length];
                    Core.Sma(closes, 0, closes.Length - 1, sma200, out _, out _, 200);
                    data.SMA200 = (decimal)sma200.Last();

                    // OBV
                    double[] obv = new double[closes.Length];
                    Core.Obv(closes, volumes, 0, closes.Length - 1, obv, out _, out _);
                    data.OBV = (decimal)obv.Last();

                    // VWAP
                    data.VWAP = CalculateVwap(timeFrameData);

                    // Order Book Imbalance
                    if (_lastOrderBooks.TryGetValue(data.Symbol, out var book))
                    {
                        data.OrderBookImbalance = CalculateOrderBookImbalance(book, data.Close);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Ошибка расчета индикаторов: {ex.Message}");
                }
            }

            private decimal CalculateVwap(List<MarketDataPoint> data)
            {
                try
                {
                    var period = data.TakeLast(30).ToList(); // 30 последних свечей
                    decimal totalVolume = period.Sum(d => d.Volume);
                    if (totalVolume == 0) return 0;

                    return period.Sum(d => d.Close * d.Volume) / totalVolume;
                }
                catch
                {
                    return 0;
                }
            }

            private decimal CalculateOrderBookImbalance(OrderBook book, decimal currentPrice)
            {
                try
                {
                    decimal bidVolume = book.Bids
                        .Where(b => b.Price >= currentPrice * 0.99m)
                        .Sum(b => b.Quantity);

                    decimal askVolume = book.Asks
                        .Where(a => a.Price <= currentPrice * 1.01m)
                        .Sum(a => a.Quantity);

                    if (bidVolume + askVolume == 0) return 0;

                    return (bidVolume - askVolume) / (bidVolume + askVolume);
                }
                catch
                {
                    return 0;
                }
            }

            public TradingSignal GenerateTaSignal(MarketDataPoint data)
            {
                try
                {
                    bool isBullish = data.RSI > 50m &&
                                    data.MACD > data.Signal &&
                                    data.Close > data.SMA50 &&
                                    data.SMA50 > data.SMA200 &&
                                    data.OrderBookImbalance > 0.2m;

                    bool isBearish = data.RSI < 50m &&
                                     data.MACD < data.Signal &&
                                     data.Close < data.SMA50 &&
                                     data.SMA50 < data.SMA200 &&
                                     data.OrderBookImbalance < -0.2m;

                    var direction = isBullish ? TradeDirection.Long :
                                    isBearish ? TradeDirection.Short :
                                    data.RSI > 50m ? TradeDirection.Long : TradeDirection.Short;

                    var confidenceFactors = new List<decimal>
                {
                    Math.Abs(data.RSI - 50m) / 50m,
                    Math.Abs(data.MACD - data.Signal) / (data.Signal != 0 ? data.Signal : 1m),
                    (data.Close - data.SMA50) / data.SMA50 * 10m,
                    Math.Abs(data.OrderBookImbalance) * 2m
                };

                    var confidence = confidenceFactors.Average();

                    return new TradingSignal
                    {
                        Symbol = data.Symbol,
                        Direction = direction,
                        Confidence = Math.Min(1m, confidence),
                        Timestamp = DateTime.UtcNow,
                        TimeFrame = data.TimeFrame,
                        Features = new Dictionary<string, object>
                    {
                        { "RSI", data.RSI },
                        { "MACD", data.MACD },
                        { "ATR", data.ATR },
                        { "Volume", data.Volume },
                        { "OBV", data.OBV },
                        { "OrderBookImbalance", data.OrderBookImbalance }
                    }
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

                    var sma50 = dailyData.TakeLast(50).Average(d => d.Close);
                    var sma200 = dailyData.TakeLast(200).Average(d => d.Close);

                    var last5Days = dailyData.TakeLast(5).ToList();
                    var bullDays = last5Days.Count(d => d.Close > d.Open);
                    var bearDays = last5Days.Count(d => d.Close < d.Open);

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

            public OrderBookAnalysis AnalyzeOrderBook(OrderBook book)
            {
                try
                {
                    var analysis = new OrderBookAnalysis();

                    // Уровни поддержки/сопротивления
                    analysis.SupportLevels = book.Bids
                        .GroupBy(b => Math.Round(b.Price, 2))
                        .OrderByDescending(g => g.Sum(b => b.Quantity))
                        .Take(3)
                        .Select(g => g.Key)
                        .ToList();

                    analysis.ResistanceLevels = book.Asks
                        .GroupBy(a => Math.Round(a.Price, 2))
                        .OrderByDescending(g => g.Sum(a => a.Quantity))
                        .Take(3)
                        .Select(g => g.Key)
                        .ToList();

                    // Кластеры ликвидности
                    analysis.BidClusters = FindLiquidityClusters(book.Bids);
                    analysis.AskClusters = FindLiquidityClusters(book.Asks);

                    return analysis;
                }
                catch
                {
                    return new OrderBookAnalysis();
                }
            }

            private List<LiquidityCluster> FindLiquidityClusters(List<OrderBookEntry> entries)
            {
                if (!entries.Any()) return new List<LiquidityCluster>();

                var clusters = new List<LiquidityCluster>();
                var currentCluster = new LiquidityCluster
                {
                    MinPrice = entries[0].Price,
                    MaxPrice = entries[0].Price
                };

                foreach (var entry in entries)
                {
                    if (entry.Price <= currentCluster.MaxPrice * 1.001m)
                    {
                        currentCluster.MaxPrice = entry.Price;
                        currentCluster.TotalQuantity += entry.Quantity;
                    }
                    else
                    {
                        clusters.Add(currentCluster);
                        currentCluster = new LiquidityCluster
                        {
                            MinPrice = entry.Price,
                            MaxPrice = entry.Price,
                            TotalQuantity = entry.Quantity
                        };
                    }
                }

                clusters.Add(currentCluster);
                return clusters
                    .OrderByDescending(c => c.TotalQuantity)
                    .Take(3)
                    .ToList();
            }

            public decimal ForecastVolatility(string symbol, int periods)
            {
                try
                {
                    var data = GetLatestData(KlineInterval.OneHour)
                        .Where(d => d.Symbol == symbol)
                        .TakeLast(100)
                        .ToList();

                    if (data.Count < 50) return 0m;

                    // Простая модель на основе исторической волатильности
                    decimal sum = 0;
                    int count = 0;

                    for (int i = 5; i <= 20; i++)
                    {
                        sum += CalculateVolatility(data, i);
                        count++;
                    }

                    return sum / count;
                }
                catch
                {
                    return 0m;
                }
            }
        }

        public class OrderBookAnalysis
        {
            public List<decimal> SupportLevels { get; set; } = new();
            public List<decimal> ResistanceLevels { get; set; } = new();
            public List<LiquidityCluster> BidClusters { get; set; } = new();
            public List<LiquidityCluster> AskClusters { get; set; } = new();
        }

        public class LiquidityCluster
        {
            public decimal MinPrice { get; set; }
            public decimal MaxPrice { get; set; }
            public decimal TotalQuantity { get; set; }
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

        /// <summary></summary>
        public class OnlineModelTrainer
        {
            private readonly MLContext _mlContext;
            private readonly int _lookbackWindow;
            private ITransformer _model;
            private PredictionEngine<MarketDataPoint, PricePrediction> _predictionEngine;
            private readonly ILogger _logger;
            private readonly object _modelLock = new();
            private DateTime _lastRetrainTime = DateTime.MinValue;

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
                            nameof(MarketDataPoint.Volume),
                            nameof(MarketDataPoint.OBV),
                            nameof(MarketDataPoint.OrderBookImbalance))
                        .Append(_mlContext.Regression.Trainers.LightGbm(
                            new Microsoft.ML.Trainers.LightGbm.LightGbmRegressionTrainer.Options
                            {
                                NumberOfIterations = 100,
                                LearningRate = 0.1,
                                NumberOfLeaves = 20,
                                UseCategoricalSplit = true,
                                HandleMissingValue = true,
                                MinimumExampleCountPerLeaf = 10,
                                FeatureFraction = 0.8,
                                BaggingFraction = 0.8,
                                BaggingFreq = 10
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
                if (newData.Count < _lookbackWindow ||
                    (DateTime.UtcNow - _lastRetrainTime).TotalHours < 1)
                    return;

                try
                {
                    var dataView = _mlContext.Data.LoadFromEnumerable(newData.TakeLast(_lookbackWindow));

                    // Разделение на обучающую и тестовую выборки
                    var trainTestSplit = _mlContext.Data.TrainTestSplit(dataView, testFraction: 0.2);

                    var newModel = await Task.Run(() =>
                    {
                        // Проверка на переобучение
                        var cvResults = _mlContext.Regression.CrossValidate(
                            trainTestSplit.TrainSet,
                            _model.GetPipeline(),
                            numberOfFolds: 5);

                        var avgRSquared = cvResults.Average(r => r.Metrics.RSquared);
                        if (avgRSquared < 0.7)
                        {
                            _logger?.LogWarning($"Возможно переобучение модели. R²: {avgRSquared:F2}");
                        }

                        // Тестирование на отложенной выборке
                        var testMetrics = _mlContext.Regression.Evaluate(
                            _model.Transform(trainTestSplit.TestSet));

                        if (testMetrics.RSquared < 0.6)
                        {
                            _logger?.LogWarning($"Низкое качество на тестовой выборке: {testMetrics.RSquared:F2}");
                            return _model; // Возвращаем старую модель
                        }

                        return _model.ContinueTrain(trainTestSplit.TrainSet);
                    });

                    lock (_modelLock)
                    {
                        _model = newModel;
                        _predictionEngine = _mlContext.Model.CreatePredictionEngine<MarketDataPoint, PricePrediction>(_model);
                        _lastRetrainTime = DateTime.UtcNow;
                    }
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
                    PricePrediction prediction;
                    lock (_modelLock)
                    {
                        prediction = _predictionEngine.Predict(data);
                    }

                    return new TradingSignal
                    {
                        StrategyId = "ML_Model",
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
                        { "Volume", data.Volume },
                        { "OBV", data.OBV },
                        { "OrderBookImbalance", data.OrderBookImbalance },
                        { "PredictedChange", prediction.FuturePriceChange }
                    }
                    };
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка предсказания");
                    return new TradingSignal
                    {
                        StrategyId = "ML_Model",
                        Symbol = data.Symbol,
                        Direction = TradeDirection.Long,
                        Confidence = 0,
                        Timestamp = DateTime.UtcNow,
                        TimeFrame = data.TimeFrame
                    };
                }
            }

            public ModelValidationResult ValidateModel(IDataView testData)
            {
                try
                {
                    var predictions = _model.Transform(testData);
                    var metrics = _mlContext.Regression.Evaluate(predictions);

                    // Проверка на look-ahead bias
                    var lookAheadCheck = CheckLookAheadBias(testData);

                    return new ModelValidationResult
                    {
                        RSquared = metrics.RSquared,
                        MeanAbsoluteError = metrics.MeanAbsoluteError,
                        IsLookAheadBiasDetected = lookAheadCheck,
                        IsValid = metrics.RSquared > 0.6 && !lookAheadCheck
                    };
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка валидации модели");
                    return new ModelValidationResult { IsValid = false };
                }
            }

            private bool CheckLookAheadBias(IDataView data)
            {
                // Упрощенная проверка на look-ahead
                // В реальной реализации нужно сравнивать с out-of-sample тестами
                var cvResults = _mlContext.Regression.CrossValidate(data, _model.GetPipeline(), 5);
                var avgDiff = cvResults.Average(r => r.Metrics.RSquared - r.Metrics.LossFunction);

                return avgDiff > 0.3; // Эмпирический порог
            }

            public FeatureImportance[] GetFeatureImportance()
            {
                try
                {
                    var permutationMetrics = _mlContext.Regression
                        .PermutationFeatureImportance(_model, _mlContext.Data.LoadFromEnumerable(new List<MarketDataPoint>()));

                    return permutationMetrics
                        .Select((metric, index) => new FeatureImportance
                        {
                            FeatureName = _model.GetInputSchema()[index].Name,
                            ImportanceScore = metric.RSquared
                        })
                        .OrderByDescending(f => f.ImportanceScore)
                        .ToArray();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета важности признаков");
                    return Array.Empty<FeatureImportance>();
                }
            }

            public async Task<RetrainResult> RetrainModel(List<MarketDataPoint> newData)
            {
                try
                {
                    var fullData = _mlContext.Data.LoadFromEnumerable(newData.TakeLast(_lookbackWindow * 2));
                    var split = _mlContext.Data.TrainTestSplit(fullData, 0.3);

                    var newModel = await Task.Run(() =>
                        _mlContext.Regression.Trainers.LightGbm()
                            .Fit(split.TrainSet));

                    var metrics = _mlContext.Regression.Evaluate(newModel.Transform(split.TestSet));

                    return new RetrainResult
                    {
                        Model = newModel,
                        TestData = split.TestSet,
                        Metrics = metrics,
                        IsSuccessful = metrics.RSquared > 0.65
                    };
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка переобучения модели");
                    return new RetrainResult { IsSuccessful = false };
                }
            }
        }

       

        public class ModelValidationResult
        {
            public double RSquared { get; set; }
            public double MeanAbsoluteError { get; set; }
            public bool IsLookAheadBiasDetected { get; set; }
            public bool IsValid { get; set; }
        }

        public class FeatureImportance
        {
            public string FeatureName { get; set; }
            public double ImportanceScore { get; set; }
        }

        public class RetrainResult
        {
            public ITransformer Model { get; set; }
            public IDataView TestData { get; set; }
            public RegressionMetrics Metrics { get; set; }
            public bool IsSuccessful { get; set; }
        }

        /// <summary></summary>
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
                string filter = null)
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

                var marketData = ProcessMarketData(klinesResult.Data, filter);

                return ExecuteBacktest(marketData, symbol, interval);
            }

            private List<MarketDataPoint> ProcessMarketData(IEnumerable<BinanceKline> klines, string filter)
            {
                var data = klines.Select(k => new MarketDataPoint
                {
                    Symbol = k.Symbol,
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
                    data = data
                        .Where(d => (d.High - d.Low) / d.Close > 0.02m)
                        .ToList();
                }
                else if (filter == "LowLiquidity")
                {
                    foreach (var d in data)
                    {
                        d.Close *= 1.001m; // Имитация проскальзывания
                    }
                }

                // Расчет индикаторов
                foreach (var point in data)
                {
                    _marketDataProcessor.CalculateIndicators(point);
                }

                return data;
            }

            private BacktestResult ExecuteBacktest(List<MarketDataPoint> data, string symbol, KlineInterval interval)
            {
                decimal balance = 10000m;
                int wins = 0, losses = 0;
                decimal maxBalance = balance;
                decimal maxDrawdown = 0m;
                var returns = new List<decimal>();
                var trades = new List<TradeRecord>();
                var equityCurve = new List<decimal>();

                for (int i = 50; i < data.Count - 1; i++)
                {
                    var current = data[i];
                    var signal = _marketDataProcessor.GenerateTaSignal(current);

                    if (signal.Confidence > 0.7m)
                    {
                        decimal entry = current.Close;
                        decimal exit = data[i + 1].Close;

                        // Эмуляция исполнения
                        decimal commission = entry * 0.001m * 2;
                        decimal slippage = entry * 0.0005m * (signal.Direction == TradeDirection.Long ? 1 : -1);

                        decimal pnl = (exit - entry) * (signal.Direction == TradeDirection.Long ? 1 : -1);
                        decimal netPnl = pnl - commission - slippage;

                        // Обновление баланса
                        balance += netPnl;
                        returns.Add(netPnl / 10000m);
                        equityCurve.Add(balance);

                        // Обновление метрик
                        maxBalance = Math.Max(maxBalance, balance);
                        maxDrawdown = Math.Max(maxDrawdown, (maxBalance - balance) / maxBalance);

                        if (netPnl > 0) wins++; else losses++;

                        // Сохранение сделки
                        trades.Add(new TradeRecord
                        {
                            Symbol = symbol,
                            Side = signal.Direction == TradeDirection.Long ? "BUY" : "SELL",
                            Quantity = 10000m / entry,
                            EntryPrice = entry,
                            ExitPrice = exit,
                            EntryTime = current.OpenTime,
                            ExitTime = data[i + 1].OpenTime,
                            Profit = netPnl,
                            Commission = commission,
                            Slippage = slippage,
                            TimeFrame = interval.ToString(),
                            StrategyId = "Backtest_TA"
                        });
                    }
                }

                return new BacktestResult
                {
                    Success = true,
                    SharpeRatio = CalculateSharpeRatio(returns),
                    SortinoRatio = CalculateSortinoRatio(returns),
                    ProfitFactor = CalculateProfitFactor(trades),
                    TotalReturn = (balance - 10000m) / 10000m,
                    MaxDrawdown = maxDrawdown,
                    WinRate = trades.Count > 0 ? (decimal)wins / trades.Count : 0m,
                    TimeFrame = interval.ToString(),
                    Trades = trades,
                    EquityCurve = equityCurve,
                    StabilityIndex = CalculateStabilityIndex(equityCurve)
                };
            }

            public SlippageAnalysis TestSlippage(string symbol, decimal orderSize)
            {
                try
                {
                    var orderBook = _restClient.SpotApi.ExchangeData.GetOrderBookAsync(symbol, 1000).Result;
                    if (!orderBook.Success)
                        return new SlippageAnalysis { Success = false };

                    decimal remaining = orderSize;
                    decimal totalCost = 0;
                    decimal totalQuantity = 0;

                    foreach (var ask in orderBook.Data.Asks.OrderBy(a => a.Price))
                    {
                        decimal quantity = Math.Min(remaining, ask.Quantity);
                        totalCost += quantity * ask.Price;
                        totalQuantity += quantity;
                        remaining -= quantity;

                        if (remaining <= 0) break;
                    }

                    if (remaining > 0)
                    {
                        return new SlippageAnalysis
                        {
                            Success = false,
                            Error = "Недостаточная ликвидность"
                        };
                    }

                    decimal avgPrice = totalCost / totalQuantity;
                    decimal slippage = (avgPrice - orderBook.Data.Asks.First().Price) / orderBook.Data.Asks.First().Price;

                    return new SlippageAnalysis
                    {
                        Success = true,
                        Symbol = symbol,
                        OrderSize = orderSize,
                        AveragePrice = avgPrice,
                        SlippagePercent = slippage * 100,
                        LiquidityRequired = totalCost
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка тестирования проскальзывания");
                    return new SlippageAnalysis { Success = false, Error = ex.Message };
                }
            }

            public MonteCarloResult RunMonteCarloSimulation(List<TradeRecord> trades, int iterations = 1000)
            {
                var result = new MonteCarloResult();
                var random = new Random();
                var equityCurves = new List<List<decimal>>();

                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        decimal balance = 10000m;
                        var shuffledTrades = trades.OrderBy(x => random.Next()).ToList();
                        var curve = new List<decimal>();

                        foreach (var trade in shuffledTrades)
                        {
                            balance += trade.Profit ?? 0;
                            curve.Add(balance);
                        }

                        equityCurves.Add(curve);
                    }

                    // Анализ результатов
                    result.Success = true;
                    result.MinFinalBalance = equityCurves.Min(c => c.Last());
                    result.MaxFinalBalance = equityCurves.Max(c => c.Last());
                    result.AvgFinalBalance = equityCurves.Average(c => c.Last());
                    result.ProbabilityOfProfit = equityCurves.Count(c => c.Last() > 10000m) / (decimal)iterations;
                    result.MaxDrawdownDistribution = equityCurves
                        .Select(CalculateMaxDrawdown)
                        .GroupBy(d => Math.Round(d, 2))
                        .ToDictionary(g => g.Key, g => g.Count());
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка Monte-Carlo симуляции");
                    result.Success = false;
                    result.Error = ex.Message;
                }

                return result;
            }

            private decimal CalculateSharpeRatio(List<decimal> returns)
            {
                if (returns.Count == 0) return 0m;

                var avgReturn = returns.Average();
                var stdDev = (decimal)Math.Sqrt(returns.Select(r => Math.Pow((double)(r - avgReturn), 2)).Average());

                return stdDev != 0 ? avgReturn / stdDev * (decimal)Math.Sqrt(365) : 0m;
            }

            private decimal CalculateSortinoRatio(List<decimal> returns)
            {
                if (returns.Count == 0) return 0m;

                var avgReturn = returns.Average();
                var downsideStdDev = (decimal)Math.Sqrt(
                    returns.Where(r => r < 0)
                    .Select(r => Math.Pow((double)r, 2))
                    .Average());

                return downsideStdDev != 0 ? avgReturn / downsideStdDev * (decimal)Math.Sqrt(365) : 0m;
            }

            private decimal CalculateProfitFactor(List<TradeRecord> trades)
            {
                var grossProfit = trades.Where(t => t.Profit > 0).Sum(t => t.Profit) ?? 0m;
                var grossLoss = Math.Abs(trades.Where(t => t.Profit < 0).Sum(t => t.Profit) ?? 0m);

                return grossLoss != 0 ? grossProfit / grossLoss : 0m;
            }

            private decimal CalculateStabilityIndex(List<decimal> equityCurve)
            {
                if (equityCurve.Count < 2) return 1m;

                decimal sum = 0;
                for (int i = 1; i < equityCurve.Count; i++)
                {
                    sum += Math.Abs(equityCurve[i] - equityCurve[i - 1]) / equityCurve[i - 1];
                }

                return 1 - (sum / (equityCurve.Count - 1));
            }

            private decimal CalculateMaxDrawdown(List<decimal> equityCurve)
            {
                decimal peak = equityCurve[0];
                decimal maxDrawdown = 0;

                foreach (var value in equityCurve)
                {
                    if (value > peak) peak = value;
                    decimal drawdown = (peak - value) / peak;
                    if (drawdown > maxDrawdown) maxDrawdown = drawdown;
                }

                return maxDrawdown;
            }
        }

        public class SlippageAnalysis
        {
            public bool Success { get; set; }
            public string Symbol { get; set; }
            public decimal OrderSize { get; set; }
            public decimal AveragePrice { get; set; }
            public decimal SlippagePercent { get; set; }
            public decimal LiquidityRequired { get; set; }
            public string Error { get; set; }
        }

        public class MonteCarloResult
        {
            public bool Success { get; set; }
            public decimal MinFinalBalance { get; set; }
            public decimal MaxFinalBalance { get; set; }
            public decimal AvgFinalBalance { get; set; }
            public decimal ProbabilityOfProfit { get; set; }
            public Dictionary<decimal, int> MaxDrawdownDistribution { get; set; }
            public string Error { get; set; }
        }

        /// <summary> </summary>
        public class NewsMonitor
        {
            private readonly ILogger _logger;
            private readonly ConcurrentDictionary<string, NewsEvent> _activeNews = new();
            private readonly HttpClient _httpClient;
            private Timer _monitoringTimer;
            private bool _isMonitoring;
            private readonly string[] _trustedSources = { "Coindesk", "Cointelegraph", "Binance Blog" };

            public NewsMonitor(ILogger logger)
            {
                _logger = logger;
                _httpClient = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(10)
                };
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
                    var newEvents = await FetchNewsEvents();
                    await ProcessNewEvents(newEvents);
                    CleanupExpiredEvents();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка мониторинга новостей");
                }
            }

            private async Task<List<NewsEvent>> FetchNewsEvents()
            {
                try
                {
                    // Интеграция с CryptoPanic API
                    var response = await _httpClient.GetAsync(
                        "https://cryptopanic.com/api/v1/posts/?auth_token=YOUR_API_KEY&currencies=BTC,ETH,BNB");

                    if (!response.IsSuccessStatusCode)
                        return new List<NewsEvent>();

                    var content = await response.Content.ReadAsStringAsync();
                    var apiResponse = JsonConvert.DeserializeObject<CryptoPanicResponse>(content);

                    return apiResponse.Results.Select(n => new NewsEvent
                    {
                        NewsId = n.Id.ToString(),
                        Title = n.Title,
                        Source = MapSource(n.Source.Domain),
                        PublishedAt = DateTime.Parse(n.PublishedAt),
                        ExpiresAt = DateTime.Parse(n.PublishedAt).AddHours(6),
                        ImpactLevel = CalculateImpactLevel(n),
                        Symbol = n.Currencies.FirstOrDefault()?.Code ?? "GENERAL",
                        IsVerified = _trustedSources.Contains(n.Source.Title)
                    }).ToList();
                }
                catch
                {
                    return new List<NewsEvent>();
                }
            }

            private async Task ProcessNewEvents(List<NewsEvent> newEvents)
            {
                foreach (var news in newEvents)
                {
                    if (_activeNews.ContainsKey(news.NewsId))
                        continue;

                    // Анализ тональности
                    news.SentimentScore = await AnalyzeSentiment(news.Title);
                    _activeNews[news.NewsId] = news;

                    if (news.ImpactLevel >= 3)
                    {
                        _logger.LogWarning($"Важная новость: {news.Title} (Impact: {news.ImpactLevel})");
                    }
                }
            }

            private async Task<decimal> AnalyzeSentiment(string text)
            {
                try
                {
                    // Упрощенная реализация - в продакшене использовать NLP API
                    var negativeWords = new[] { "crash", "drop", "bear", "fraud", "hack" };
                    var positiveWords = new[] { "rise", "bull", "adopt", "institutional", "partnership" };

                    decimal score = 0;
                    score += positiveWords.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)) * 0.1m;
                    score -= negativeWords.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)) * 0.1m;

                    return Math.Clamp(score, -1, 1);
                }
                catch
                {
                    return 0;
                }
            }

            private int CalculateImpactLevel(dynamic newsItem)
            {
                // Эвристический расчет важности новости
                int impact = 1;

                if (newsItem.Votes.Important > 5) impact++;
                if (newsItem.Source.Title.Contains("Bloomberg")) impact++;
                if (newsItem.Title.Contains("BTC") || newsItem.Title.Contains("Bitcoin")) impact++;

                return Math.Clamp(impact, 1, 5);
            }

            private void CleanupExpiredEvents()
            {
                var expired = _activeNews.Where(kv => kv.Value.ExpiresAt < DateTime.UtcNow).ToList();
                foreach (var kv in expired)
                {
                    _activeNews.TryRemove(kv.Key, out _);
                }
            }

            private string MapSource(string domain)
            {
                return domain switch
                {
                    var d when d.Contains("coindesk") => "Coindesk",
                    var d when d.Contains("cointelegraph") => "Cointelegraph",
                    var d when d.Contains("binance") => "Binance Blog",
                    _ => "Other"
                };
            }

            public bool IsHighImpactNewsPending()
            {
                return _activeNews.Any(kv => kv.Value.ImpactLevel >= 3);
            }

            public List<string> GetAffectedSymbols()
            {
                return _activeNews
                    .Where(kv => kv.Value.ImpactLevel >= 3)
                    .Select(kv => kv.Value.Symbol)
                    .Distinct()
                    .ToList();
            }

            public bool IsSymbolAffected(string symbol)
            {
                return _activeNews.Any(kv =>
                    kv.Value.ImpactLevel >= 2 &&
                    (kv.Value.Symbol == symbol || kv.Value.Symbol == "GENERAL"));
            }

            public List<NewsEvent> GetRecentNews(int count)
            {
                return _activeNews.Values
                    .OrderByDescending(n => n.ImpactLevel)
                    .ThenByDescending(n => n.PublishedAt)
                    .Take(count)
                    .ToList();
            }

            private class CryptoPanicResponse
            {
                public List<CryptoPanicNews> Results { get; set; }
            }

            private class CryptoPanicNews
            {
                public int Id { get; set; }
                public string Title { get; set; }
                public string PublishedAt { get; set; }
                public CryptoPanicSource Source { get; set; }
                public List<CryptoPanicCurrency> Currencies { get; set; }
                public CryptoPanicVotes Votes { get; set; }
            }

            private class CryptoPanicSource
            {
                public string Title { get; set; }
                public string Domain { get; set; }
            }

            private class CryptoPanicCurrency
            {
                public string Code { get; set; }
            }

            private class CryptoPanicVotes
            {
                public int Important { get; set; }
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

            // Технические индикаторы
            public decimal RSI { get; set; }
            public decimal MACD { get; set; }
            public decimal Signal { get; set; } // Добавлено для MACD
            public decimal ATR { get; set; }
            public decimal SMA50 { get; set; }
            public decimal SMA200 { get; set; }

            // Новые поля:
            public decimal OBV { get; set; } // On-Balance Volume для анализа объема
            public decimal VWAP { get; set; } // Средневзвешенная цена
            public decimal OrderBookImbalance { get; set; } // Дисбаланс стакана
        }

        public class TradingSignal
        {
            public string Symbol { get; set; } = string.Empty;
            public TradeDirection Direction { get; set; }
            public decimal Confidence { get; set; }
            public DateTime Timestamp { get; set; }
            public KlineInterval TimeFrame { get; set; }

            // Новые поля:
            public string StrategyId { get; set; } // Идентификатор стратегии
            public decimal SuggestedPositionSize { get; set; } // Расчетный размер позиции
            public Dictionary<string, object> Features { get; set; } = new(); // Доп. признаки для ML

            // Для VWAP-исполнения
            public bool UseVwap { get; set; }
            public int VwapDurationMinutes { get; set; } = 5;
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

            // Новые поля:
            public decimal PortfolioBeta { get; set; } // Бета портфеля
            public decimal StressTestResult { get; set; } // Результат стресс-теста
            public decimal MarginUsage { get; set; } // Использование маржи
            public decimal DailyProfitLoss { get; set; } // Дневной PnL
        }

        public class OrderRequest
        {
            public string Symbol { get; set; } = string.Empty;
            public OrderSide Side { get; set; }
            public decimal Quantity { get; set; }
            public decimal Price { get; set; }
            public decimal StopLoss { get; set; }
            public decimal TakeProfit { get; set; }

            // Новые поля:
            public string StrategyId { get; set; } // Для связи со стратегией
            public bool UseVwap { get; set; } // Флаг VWAP-исполнения
            public bool UseIceberg { get; set; } // Айсберг-ордера
            public decimal MaxSlippagePercent { get; set; } = 0.1m;
            public KlineInterval TimeFrame { get; set; } // Таймфрейм сигнала
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

            // Новые поля:
            public string StrategyId { get; set; } // Для связи со стратегией
            public decimal CurrentRiskRewardRatio { get; set; } // Текущее RR
            public bool IsHedged { get; set; } // Флаг хеджирования
            public string HedgeSymbol { get; set; } // Пара для хеджа
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
            [ColumnName("Score")]
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

            // Новые метрики:
            public decimal SortinoRatio { get; set; }
            public decimal ProfitFactor { get; set; }
            public decimal AvgTradeDuration { get; set; } // в минутах
            public decimal WorstTrade { get; set; }
            public decimal BestTrade { get; set; }
            public decimal StabilityIndex { get; set; } // Стабильность доходности
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

            // Новые поля:
            public string StrategyId { get; set; } // Для анализа стратегий
            public decimal InitialRiskReward { get; set; } // Планируемое RR
            public decimal RealizedRiskReward { get; set; } // Фактическое RR
            public int ExecutionSeconds { get; set; } // Время исполнения
        }

        public class NewsEvent
        {
            public string Symbol { get; set; }
            public string Title { get; set; }

            // Расширенные поля:
            public NewsSource Source { get; set; } // Источник новости
            public string[] RelatedAssets { get; set; } // Связанные активы
            public decimal SentimentScore { get; set; } // -1 до +1
            public int ImpactLevel { get; set; } // 1-5
            public DateTime PublishedAt { get; set; }
            public DateTime ExpiresAt { get; set; }

            // Новые поля:
            public bool IsVerified { get; set; } // Подтвержденная новость
            public string NewsId { get; set; } // Уникальный ID
            public decimal MarketReaction { get; set; } // % изменения цены после новости
        }

        public enum NewsSource
        {
            CryptoPanic,
            TradingView,
            Twitter,
            OfficialAnnouncement,
            Other
        }
        #endregion
    }
}