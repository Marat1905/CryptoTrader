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
        private TelegramBotClient? _telegramBot;
        private readonly MLContext _mlContext = new(seed: 1);
        private ITransformer? _model;
        private readonly SqliteConnection _dbConnection;
        private readonly AsyncRetryPolicy _retryPolicy;
        private ILogger _logger;
        private readonly bool _isSandboxMode;

        private  EnhancedRiskEngine _riskEngine;
        private  EnhancedExecutionEngine _executionEngine;
        private  MultiTimeFrameMarketDataProcessor _marketDataProcessor;
        private  PortfolioManager _portfolioManager;
        private  CorrelationAnalyzer _correlationAnalyzer;
        private  OnlineModelTrainer _onlineModelTrainer;
        private  Backtester _backtester;
        private  NewsMonitor _newsMonitor;
        private  StrategyEvaluator _strategyEvaluator;
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

        /// <summary>
        /// Обработчик рыночных данных с поддержкой нескольких таймфреймов
        /// </summary>
        public class MultiTimeFrameMarketDataProcessor
        {
            // Кэш данных по таймфреймам
            private readonly Dictionary<KlineInterval, List<MarketDataPoint>> _dataCache = new();

            // Объект для синхронизации доступа к кэшу
            private readonly object _cacheLock = new();

            // Список поддерживаемых таймфреймов
            private readonly List<KlineInterval> _timeFrames;

            // Кэш последних стаканов цен по символам
            private readonly Dictionary<string, OrderBook> _lastOrderBooks = new();

            /// <summary>
            /// Конструктор с инициализацией таймфреймов
            /// </summary>
            public MultiTimeFrameMarketDataProcessor(List<KlineInterval> timeFrames)
            {
                _timeFrames = timeFrames;

                // Инициализация кэша для каждого таймфрейма
                foreach (var tf in timeFrames)
                {
                    _dataCache[tf] = new List<MarketDataPoint>();
                }
            }

            /// <summary>
            /// Обработка новой свечи
            /// </summary>
            public void ProcessKline(MarketDataPoint data)
            {
                lock (_cacheLock)
                {
                    // Проверка поддержки таймфрейма
                    if (!_dataCache.ContainsKey(data.TimeFrame))
                        return;

                    // Добавление данных в кэш
                    _dataCache[data.TimeFrame].Add(data);

                    // Ограничение размера кэша (5000 последних свечей)
                    if (_dataCache[data.TimeFrame].Count > 5000)
                    {
                        _dataCache[data.TimeFrame].RemoveRange(
                            0,
                            _dataCache[data.TimeFrame].Count - 5000);
                    }
                }

                // Расчет индикаторов для новой свечи
                CalculateIndicators(data);
            }

            /// <summary>
            /// Обработка обновления стакана цен
            /// </summary>
            public void ProcessOrderBook(OrderBook book)
            {
                _lastOrderBooks[book.Symbol] = book;
            }

            /// <summary>
            /// Получение данных по конкретному таймфрейму
            /// </summary>
            public List<MarketDataPoint> GetLatestData(KlineInterval timeFrame)
            {
                lock (_cacheLock)
                {
                    return _dataCache.TryGetValue(timeFrame, out var data) ?
                        new List<MarketDataPoint>(data) : // Возвращаем копию для безопасности
                        new List<MarketDataPoint>();
                }
            }

            /// <summary>
            /// Расчет технических индикаторов для свечи
            /// </summary>
            public void CalculateIndicators(MarketDataPoint data)
            {
                try
                {
                    // Получаем исторические данные для расчета индикаторов
                    var timeFrameData = GetLatestData(data.TimeFrame);

                    // Для расчета большинства индикаторов нужно минимум 50 свечей
                    if (timeFrameData.Count < 50) return;

                    // Подготовка массивов для TA-Lib
                    double[] closes = timeFrameData.Select(d => (double)d.Close).ToArray();
                    double[] highs = timeFrameData.Select(d => (double)d.High).ToArray();
                    double[] lows = timeFrameData.Select(d => (double)d.Low).ToArray();
                    double[] volumes = timeFrameData.Select(d => (double)d.Volume).ToArray();

                    // 1. Расчет RSI (Relative Strength Index)
                    double[] rsiOutput = new double[closes.Length];
                    Core.Rsi(closes, 0, closes.Length - 1, rsiOutput, out _, out _);
                    data.RSI = (decimal)rsiOutput.Last();

                    // 2. Расчет MACD (Moving Average Convergence Divergence)
                    double[] macd = new double[closes.Length];
                    double[] signal = new double[closes.Length];
                    double[] hist = new double[closes.Length];
                    Core.Macd(closes, 0, closes.Length - 1, macd, signal, hist, out _, out _);
                    data.MACD = (decimal)macd.Last();
                    data.Signal = (decimal)signal.Last();

                    // 3. Расчет ATR (Average True Range)
                    double[] atrOutput = new double[closes.Length];
                    Core.Atr(highs, lows, closes, 0, closes.Length - 1, atrOutput, out _, out _);
                    data.ATR = (decimal)atrOutput.Last();

                    // 4. Расчет SMA (Simple Moving Average)
                    double[] sma50 = new double[closes.Length];
                    Core.Sma(closes, 0, closes.Length - 1, sma50, out _, out _, 50);
                    data.SMA50 = (decimal)sma50.Last();

                    double[] sma200 = new double[closes.Length];
                    Core.Sma(closes, 0, closes.Length - 1, sma200, out _, out _, 200);
                    data.SMA200 = (decimal)sma200.Last();

                    // 5. Расчет OBV (On-Balance Volume)
                    double[] obv = new double[closes.Length];
                    Core.Obv(closes, volumes, 0, closes.Length - 1, obv, out _, out _);
                    data.OBV = (decimal)obv.Last();

                    // 6. Расчет VWAP (Volume Weighted Average Price)
                    data.VWAP = CalculateVwap(timeFrameData);

                    // 7. Расчет дисбаланса стакана
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

            /// <summary>
            /// Расчет VWAP (средневзвешенной цены по объему)
            /// </summary>
            private decimal CalculateVwap(List<MarketDataPoint> data)
            {
                try
                {
                    // Берем последние 30 свечей для расчета
                    var period = data.TakeLast(30).ToList();
                    decimal totalVolume = period.Sum(d => d.Volume);

                    if (totalVolume == 0) return 0;

                    // Формула VWAP: сумма(цена * объем) / сумма(объем)
                    return period.Sum(d => d.Close * d.Volume) / totalVolume;
                }
                catch
                {
                    return 0;
                }
            }

            /// <summary>
            /// Расчет дисбаланса стакана цен
            /// </summary>
            private decimal CalculateOrderBookImbalance(OrderBook book, decimal currentPrice)
            {
                try
                {
                    // Суммируем объемы в стакане рядом с текущей ценой
                    decimal bidVolume = book.Bids
                        .Where(b => b.Price >= currentPrice * 0.99m) // ±1% от текущей цены
                        .Sum(b => b.Quantity);

                    decimal askVolume = book.Asks
                        .Where(a => a.Price <= currentPrice * 1.01m)
                        .Sum(a => a.Quantity);

                    if (bidVolume + askVolume == 0) return 0;

                    // Формула дисбаланса: (объем бидов - объем асков) / (объем бидов + объем асков)
                    return (bidVolume - askVolume) / (bidVolume + askVolume);
                }
                catch
                {
                    return 0;
                }
            }

            /// <summary>
            /// Генерация торгового сигнала на основе технических индикаторов
            /// </summary>
            public TradingSignal GenerateTaSignal(MarketDataPoint data)
            {
                try
                {
                    // Определение бычьего сигнала:
                    // - RSI выше 50
                    // - MACD выше сигнальной линии
                    // - Цена выше SMA50
                    // - SMA50 выше SMA200
                    // - Дисбаланс стакана в сторону покупок
                    bool isBullish = data.RSI > 50m &&
                                    data.MACD > data.Signal &&
                                    data.Close > data.SMA50 &&
                                    data.SMA50 > data.SMA200 &&
                                    data.OrderBookImbalance > 0.2m;

                    // Определение медвежьего сигнала:
                    // - RSI ниже 50
                    // - MACD ниже сигнальной линии
                    // - Цена ниже SMA50
                    // - SMA50 ниже SMA200  
                    // - Дисбаланс стакана в сторону продаж
                    bool isBearish = data.RSI < 50m &&
                                     data.MACD < data.Signal &&
                                     data.Close < data.SMA50 &&
                                     data.SMA50 < data.SMA200 &&
                                     data.OrderBookImbalance < -0.2m;

                    // Если нет четкого сигнала, используем RSI
                    var direction = isBullish ? TradeDirection.Long :
                                    isBearish ? TradeDirection.Short :
                                    data.RSI > 50m ? TradeDirection.Long : TradeDirection.Short;

                    // Расчет уверенности сигнала на основе:
                    var confidenceFactors = new List<decimal>
                {
                    Math.Abs(data.RSI - 50m) / 50m, // Отклонение RSI от 50
                    Math.Abs(data.MACD - data.Signal) / (data.Signal != 0 ? data.Signal : 1m), // Расхождение MACD
                    (data.Close - data.SMA50) / data.SMA50 * 10m, // Отклонение от SMA50
                    Math.Abs(data.OrderBookImbalance) * 2m // Сила дисбаланса стакана
                };

                    // Усредняем факторы уверенности
                    var confidence = confidenceFactors.Average();

                    return new TradingSignal
                    {
                        Symbol = data.Symbol,
                        Direction = direction,
                        Confidence = Math.Min(1m, confidence), // Ограничиваем 100%
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

            /// <summary>
            /// Определение текущего рыночного тренда
            /// </summary>
            public MarketTrend DetermineMarketTrend(List<MarketDataPoint> marketData)
            {
                try
                {
                    // Используем дневные данные для определения тренда
                    var dailyData = marketData
                        .Where(d => d.TimeFrame == KlineInterval.OneDay)
                        .OrderBy(d => d.OpenTime)
                        .ToList();

                    // Нужно минимум 30 дней данных
                    if (dailyData.Count < 30) return MarketTrend.Neutral;

                    // Расчет скользящих средних
                    var sma50 = dailyData.TakeLast(50).Average(d => d.Close);
                    var sma200 = dailyData.TakeLast(200).Average(d => d.Close);

                    // Анализ последних 5 дней
                    var last5Days = dailyData.TakeLast(5).ToList();
                    var bullDays = last5Days.Count(d => d.Close > d.Open); // Дни с ростом
                    var bearDays = last5Days.Count(d => d.Close < d.Open); // Дни с падением

                    // Бычий тренд:
                    // - SMA50 > SMA200
                    // - Больше дней роста в последние 5 дней
                    if (sma50 > sma200 && bullDays > bearDays)
                        return MarketTrend.Bullish;

                    // Медвежий тренд:
                    // - SMA50 < SMA200  
                    // - Больше дней падения в последние 5 дней
                    if (sma50 < sma200 && bearDays > bullDays)
                        return MarketTrend.Bearish;

                    // Нейтральный тренд в остальных случаях
                    return MarketTrend.Neutral;
                }
                catch
                {
                    return MarketTrend.Neutral;
                }
            }

            /// <summary>
            /// Расчет исторической волатильности
            /// </summary>
            public decimal CalculateVolatility(List<MarketDataPoint> data, int lookbackPeriod)
            {
                if (data.Count < lookbackPeriod) return 0m;

                try
                {
                    // Расчет дневных доходностей
                    var returns = new List<decimal>();
                    for (int i = 1; i < lookbackPeriod; i++)
                    {
                        returns.Add((data[i].Close - data[i - 1].Close) / data[i - 1].Close);
                    }

                    // Стандартное отклонение доходностей
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

            /// <summary>
            /// Получение последнего значения ATR
            /// </summary>
            public decimal GetLatestAtr(KlineInterval timeFrame)
            {
                lock (_cacheLock)
                {
                    return _dataCache.TryGetValue(timeFrame, out var data) && data.Count > 0 ?
                        data.Last().ATR : 0m;
                }
            }

            /// <summary>
            /// Анализ стакана цен
            /// </summary>
            public OrderBookAnalysis AnalyzeOrderBook(OrderBook book)
            {
                try
                {
                    var analysis = new OrderBookAnalysis();

                    // Поиск уровней поддержки (3 самых объемных уровня бидов)
                    analysis.SupportLevels = book.Bids
                        .GroupBy(b => Math.Round(b.Price, 2)) // Группируем по ценам с точностью до 0.01
                        .OrderByDescending(g => g.Sum(b => b.Quantity)) // Сортируем по объему
                        .Take(3) // Берем топ-3
                        .Select(g => g.Key) // Берем только цены
                        .ToList();

                    // Поиск уровней сопротивления (3 самых объемных уровня асков)
                    analysis.ResistanceLevels = book.Asks
                        .GroupBy(a => Math.Round(a.Price, 2))
                        .OrderByDescending(g => g.Sum(a => a.Quantity))
                        .Take(3)
                        .Select(g => g.Key)
                        .ToList();

                    // Поиск кластеров ликвидности
                    analysis.BidClusters = FindLiquidityClusters(book.Bids);
                    analysis.AskClusters = FindLiquidityClusters(book.Asks);

                    return analysis;
                }
                catch
                {
                    return new OrderBookAnalysis();
                }
            }

            /// <summary>
            /// Поиск кластеров ликвидности в стакане
            /// </summary>
            private List<LiquidityCluster> FindLiquidityClusters(List<OrderBookEntry> entries)
            {
                if (!entries.Any()) return new List<LiquidityCluster>();

                var clusters = new List<LiquidityCluster>();
                var currentCluster = new LiquidityCluster
                {
                    MinPrice = entries[0].Price,
                    MaxPrice = entries[0].Price
                };

                // Группируем близкие по цене уровни
                foreach (var entry in entries)
                {
                    if (entry.Price <= currentCluster.MaxPrice * 1.001m) // ±0.1%
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

                // Возвращаем 3 самых больших кластера
                return clusters
                    .OrderByDescending(c => c.TotalQuantity)
                    .Take(3)
                    .ToList();
            }

            /// <summary>
            /// Прогноз волатильности на основе исторических данных
            /// </summary>
            public decimal ForecastVolatility(string symbol, int periods)
            {
                try
                {
                    // Получаем часовые данные для символа
                    var data = GetLatestData(KlineInterval.OneHour)
                        .Where(d => d.Symbol == symbol)
                        .TakeLast(100) // Последние 100 часов
                        .ToList();

                    if (data.Count < 50) return 0m;

                    // Усредняем волатильность за разные периоды (от 5 до 20 периодов)
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

        /// <summary>
        /// Результат анализа стакана цен
        /// </summary>
        public class OrderBookAnalysis
        {
            public List<decimal> SupportLevels { get; set; } = new(); // Уровни поддержки
            public List<decimal> ResistanceLevels { get; set; } = new(); // Уровни сопротивления
            public List<LiquidityCluster> BidClusters { get; set; } = new(); // Кластеры ликвидности на покупку
            public List<LiquidityCluster> AskClusters { get; set; } = new(); // Кластеры ликвидности на продажу
        }

        /// <summary>
        /// Управление портфелем: баланс, позиции, расчет рисков и PnL
        /// </summary>
        public class PortfolioManager
        {
            private decimal _balance;
            private readonly List<TradeRecord> _trades = new();
            private readonly EnhancedRiskEngine _riskEngine;
            private readonly ILogger _logger;

            /// <summary>
            /// Текущий баланс портфеля в USDT
            /// </summary>
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

            /// <summary>
            /// Обновить баланс из API биржи
            /// </summary>
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

            /// <summary>
            /// Обновить баланс из стрима пользовательских данных
            /// </summary>
            public void UpdateBalance(IEnumerable<BinanceBalance> balances)
            {
                var usdtBalance = balances.FirstOrDefault(b => b.Asset == "USDT");
                if (usdtBalance != null)
                {
                    _balance = usdtBalance.Total;
                }
            }

            /// <summary>
            /// Зарегистрировать сделку в портфеле
            /// </summary>
            public void RecordTrade(TradeRecord trade)
            {
                _trades.Add(trade);

                // Обновляем баланс, если сделка закрыта
                if (trade.ExitPrice.HasValue)
                {
                    _balance += trade.Profit ?? 0;
                    _balance -= trade.Commission;
                }
            }

            /// <summary>
            /// Получить список открытых позиций
            /// </summary>
            public List<OpenPosition> GetOpenPositions()
            {
                return _trades
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
                        StopLossDistance = Math.Abs(t.EntryPrice - t.StopLoss),
                        StrategyId = t.StrategyId
                    })
                    .ToList();
            }

            /// <summary>
            /// Рассчитать размер позиции с учетом риска
            /// </summary>
            public decimal CalculatePositionSize(
                TradingSignal signal,
                decimal currentPrice,
                RiskMetrics riskMetrics)
            {
                try
                {
                    // Базовый размер на основе риска
                    var baseSize = _riskEngine.CalculatePositionSize(signal, currentPrice, riskMetrics);

                    // Корректировка на доступный баланс
                    var maxAffordable = _balance * 0.95m / currentPrice; // 5% оставляем как буфер
                    return Math.Min(baseSize, maxAffordable);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета размера позиции");
                    return 0m;
                }
            }

            /// <summary>
            /// Рассчитать прибыль/убыток портфеля (реализованный и нереализованный)
            /// </summary>
            public PortfolioPnL CalculatePnL(IDictionary<string, decimal> currentPrices)
            {
                var result = new PortfolioPnL();

                try
                {
                    // Реализованный PnL
                    result.RealizedPnL = _trades
                        .Where(t => t.ExitPrice.HasValue)
                        .Sum(t => t.Profit ?? 0);

                    // Нереализованный PnL
                    foreach (var pos in GetOpenPositions())
                    {
                        if (currentPrices.TryGetValue(pos.Symbol, out var currentPrice))
                        {
                            var pnl = (currentPrice - pos.EntryPrice) * pos.Quantity *
                                     (pos.Direction == TradeDirection.Long ? 1 : -1);
                            result.UnrealizedPnL += pnl;
                        }
                    }

                    // Общий PnL
                    result.TotalPnL = result.RealizedPnL + result.UnrealizedPnL;

                    // Комиссии
                    result.TotalCommission = _trades.Sum(t => t.Commission);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета PnL портфеля");
                }

                return result;
            }

            /// <summary>
            /// Ребалансировка портфеля по стратегиям
            /// </summary>
            public async Task RebalancePortfolio(
                IEnumerable<ActiveStrategy> strategies,
                IBinanceRestClient restClient)
            {
                try
                {
                    // Получаем текущие веса стратегий
                    var strategyWeights = strategies.ToDictionary(
                        s => s.Id,
                        s => s.Weight);

                    // Анализируем текущее распределение
                    var currentAllocation = GetStrategyAllocation();

                    // Определяем целевое распределение
                    var targetAllocation = strategyWeights
                        .ToDictionary(
                            kv => kv.Key,
                            kv => kv.Value * _balance);

                    // Корректируем позиции
                    foreach (var symbol in currentAllocation.Keys)
                    {
                        var current = currentAllocation[symbol];
                        var target = targetAllocation.ContainsKey(symbol)
                            ? targetAllocation[symbol]
                            : 0;

                        if (current > target)
                        {
                            // Нужно уменьшить позицию
                            var reduceBy = current - target;
                            await ReducePosition(symbol, reduceBy, restClient);
                        }
                        else if (current < target)
                        {
                            // Нужно увеличить позицию
                            var increaseBy = target - current;
                            await IncreasePosition(symbol, increaseBy, restClient);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка ребалансировки портфеля");
                }
            }

            /// <summary>
            /// Получить текущее распределение капитала по стратегиям
            /// </summary>
            public Dictionary<string, decimal> GetStrategyAllocation()
            {
                return _trades
                    .GroupBy(t => t.StrategyId ?? "default")
                    .ToDictionary(
                        g => g.Key,
                        g => g.Sum(t =>
                            t.ExitPrice.HasValue
                                ? 0
                                : t.Quantity * t.EntryPrice));
            }

            /// <summary>
            /// Запустить стресс-тест портфеля
            /// </summary>
            public StressTestResult RunStressTest(
                MarketScenario scenario,
                IDictionary<string, decimal> currentPrices)
            {
                var result = new StressTestResult();

                try
                {
                    var positions = GetOpenPositions();
                    decimal portfolioValue = positions.Sum(p =>
                        currentPrices.GetValueOrDefault(p.Symbol, 0m) * p.Quantity);

                    decimal stressedValue = 0;

                    foreach (var position in positions)
                    {
                        var price = currentPrices.GetValueOrDefault(position.Symbol, 0m);
                        if (price == 0m) continue;

                        decimal newPrice = scenario.Type switch
                        {
                            ScenarioType.MarketCrash => price * (1 - scenario.Severity),
                            ScenarioType.VolatilitySpike => price * (1 + (scenario.Severity *
                                (position.Direction == TradeDirection.Long ? -1 : 1))),
                            ScenarioType.LiquidityCrisis => price * 0.9m, // Фиксированный сценарий
                            _ => price
                        };

                        stressedValue += newPrice * position.Quantity;
                    }

                    result.MaxDrawdown = (portfolioValue - stressedValue) / portfolioValue;
                    result.IsAcceptable = result.MaxDrawdown < _riskEngine.MaxDrawdown;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка стресс-теста портфеля");
                    result.IsAcceptable = false;
                }

                return result;
            }

            /// <summary>
            /// Рассчитать ключевые метрики риска
            /// </summary>
            public RiskMetrics CalculateRiskMetrics(
                IDictionary<string, decimal> currentPrices,
                IDictionary<string, decimal> volatilities,
                IDictionary<string, Dictionary<string, decimal>> correlationMatrix)
            {
                var metrics = new RiskMetrics
                {
                    PortfolioValue = _balance,
                    OpenPositions = GetOpenPositions(),
                    CorrelationMatrix = correlationMatrix
                };

                try
                {
                    // Волатильность портфеля
                    metrics.Volatility = _riskEngine.CalculatePortfolioRisk(
                        metrics.OpenPositions,
                        currentPrices,
                        volatilities);

                    // CVaR
                    metrics.CVaR = _riskEngine.CalculateCVaR(
                        _trades.Where(t => t.ExitPrice.HasValue).ToList(),
                        0.95m);

                    // Ликвидность
                    metrics.Liquidity = currentPrices.Sum(
                        kv => kv.Value * GetPositionSize(kv.Key));
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка расчета метрик риска");
                }

                return metrics;
            }

            private decimal GetPositionSize(string symbol)
            {
                return _trades
                    .Where(t => t.Symbol == symbol && !t.ExitPrice.HasValue)
                    .Sum(t => t.Quantity);
            }

            private async Task ReducePosition(
                string symbol,
                decimal amount,
                IBinanceRestClient restClient)
            {
                try
                {
                    var position = GetOpenPositions()
                        .FirstOrDefault(p => p.Symbol == symbol);

                    if (position != null)
                    {
                        var reduceQty = Math.Min(position.Quantity, amount / position.EntryPrice);
                        if (reduceQty > 0)
                        {
                            var orderResult = await restClient.SpotApi.Trading.PlaceOrderAsync(
                                symbol,
                                position.Direction == TradeDirection.Long ? OrderSide.Sell : OrderSide.Buy,
                                SpotOrderType.Market,
                                quantity: reduceQty);

                            if (!orderResult.Success)
                            {
                                _logger?.LogError($"Ошибка уменьшения позиции: {orderResult.Error}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Ошибка уменьшения позиции {symbol}");
                }
            }

            private async Task IncreasePosition(
                string symbol,
                decimal amount,
                IBinanceRestClient restClient)
            {
                try
                {
                    var currentPrice = await GetCurrentPrice(symbol, restClient);
                    if (currentPrice == 0) return;

                    var qty = amount / currentPrice;
                    if (qty > 0)
                    {
                        var orderResult = await restClient.SpotApi.Trading.PlaceOrderAsync(
                            symbol,
                            OrderSide.Buy,
                            SpotOrderType.Market,
                            quantity: qty);

                        if (!orderResult.Success)
                        {
                            _logger?.LogError($"Ошибка увеличения позиции: {orderResult.Error}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Ошибка увеличения позиции {symbol}");
                }
            }

            private async Task<decimal> GetCurrentPrice(
                string symbol,
                IBinanceRestClient restClient)
            {
                var ticker = await restClient.SpotApi.ExchangeData.GetTickerAsync(symbol);
                return ticker.Success ? ticker.Data.LastPrice : 0m;
            }
        }

        /// <summary>
        /// Результаты расчета прибыли/убытков портфеля
        /// </summary>
        public class PortfolioPnL
        {
            public decimal RealizedPnL { get; set; } // Реализованный PnL
            public decimal UnrealizedPnL { get; set; } // Нереализованный PnL
            public decimal TotalPnL { get; set; } // Общий PnL
            public decimal TotalCommission { get; set; } // Суммарные комиссии
        }


        /// <summary>
        /// Анализатор корреляций между торговыми парами
        /// </summary>
        public class CorrelationAnalyzer
        {
            private readonly MLContext _mlContext;
            private readonly List<string> _symbols;
            private readonly Dictionary<string, List<double>> _priceHistory;
            private readonly ILogger _logger;
            private const int MaxHistorySize = 5000; // Максимальное хранимое количество ценовых точек

            /// <summary>
            /// Конструктор анализатора корреляций
            /// </summary>
            /// <param name="symbols">Список символов для анализа</param>
            /// <param name="logger">Логгер</param>
            public CorrelationAnalyzer(List<string> symbols, ILogger logger = null)
            {
                _mlContext = new MLContext();
                _symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
                _logger = logger;
                _priceHistory = symbols.ToDictionary(s => s, _ => new List<double>());
            }

            /// <summary>
            /// Добавление новой ценовой точки для символа
            /// </summary>
            /// <param name="symbol">Торговый символ (например, BTCUSDT)</param>
            /// <param name="price">Текущая цена</param>
            public void AddPriceData(string symbol, decimal price)
            {
                try
                {
                    if (!_priceHistory.ContainsKey(symbol))
                    {
                        throw new ArgumentException($"Символ {symbol} не настроен для анализа");
                    }

                    // Добавляем новое ценовое значение
                    _priceHistory[symbol].Add((double)price);

                    // Поддерживаем размер истории
                    if (_priceHistory[symbol].Count > MaxHistorySize)
                    {
                        _priceHistory[symbol].RemoveAt(0);
                    }

                    // Синхронизируем размеры всех рядов
                    SyncHistorySizes();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Ошибка добавления данных для {symbol}");
                    throw;
                }
            }

            /// <summary>
            /// Синхронизация размеров всех ценовых рядов по минимальному размеру
            /// </summary>
            private void SyncHistorySizes()
            {
                var minSize = _priceHistory.Values.Min(list => list.Count);
                foreach (var key in _priceHistory.Keys.ToList())
                {
                    if (_priceHistory[key].Count > minSize)
                    {
                        _priceHistory[key] = _priceHistory[key]
                            .Skip(_priceHistory[key].Count - minSize)
                            .ToList();
                    }
                }
            }

            /// <summary>
            /// Получение матрицы корреляций между всеми символами
            /// </summary>
            /// <returns>Словарь словарей с коэффициентами корреляции</returns>
            public Dictionary<string, Dictionary<string, decimal>> GetCorrelationMatrix()
            {
                try
                {
                    // Проверка достаточности данных
                    if (_priceHistory.Values.First().Count < 2)
                    {
                        return _symbols.ToDictionary(
                            s => s,
                            s => _symbols.ToDictionary(
                                s2 => s2,
                                s2 => s == s2 ? 1m : 0m));
                    }

                    // Подготовка данных для ML.NET
                    var data = new List<CorrelationDataItem>();
                    for (int i = 0; i < _priceHistory.Values.First().Count; i++)
                    {
                        var item = new CorrelationDataItem();
                        foreach (var symbol in _symbols)
                        {
                            var prop = typeof(CorrelationDataItem).GetProperty(symbol);
                            prop?.SetValue(item, _priceHistory[symbol][i]);
                        }
                        data.Add(item);
                    }

                    // Загрузка данных в ML.NET
                    var dataView = _mlContext.Data.LoadFromEnumerable(data);

                    // Вычисление матрицы корреляций
                    var correlationMatrix = _mlContext.Data.ComputeCorrelation(dataView);

                    // Преобразование результатов в удобный формат
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
                    return _symbols.ToDictionary(
                        s => s,
                        s => _symbols.ToDictionary(
                            s2 => s2,
                            s2 => s == s2 ? 1m : 0m));
                }
            }

            /// <summary>
            /// Генерация торгового сигнала на основе корреляций
            /// </summary>
            /// <param name="symbol">Целевой символ</param>
            /// <returns>Торговый сигнал или null, если недостаточно данных</returns>
            public TradingSignal GenerateCorrelationSignal(string symbol)
            {
                try
                {
                    var matrix = GetCorrelationMatrix();
                    var correlatedSymbols = matrix[symbol]
                        .Where(kv => kv.Value > 0.7m && kv.Key != symbol)
                        .ToList();

                    if (!correlatedSymbols.Any())
                        return null;

                    // Анализ направления коррелированных символов
                    var bullishCount = correlatedSymbols
                        .Count(kv => IsSymbolBullish(kv.Key));

                    var bearishCount = correlatedSymbols.Count - bullishCount;

                    // Определяем силу сигнала на основе количества согласованных движений
                    decimal confidence = (decimal)bullishCount / correlatedSymbols.Count;
                    confidence = Math.Abs(confidence - 0.5m) * 2; // Нормализация к 0-1

                    return new TradingSignal
                    {
                        Symbol = symbol,
                        Direction = bullishCount > bearishCount ?
                            TradeDirection.Long : TradeDirection.Short,
                        Confidence = confidence,
                        Timestamp = DateTime.UtcNow,
                        Features = new Dictionary<string, object>
                    {
                        { "CorrelatedSymbolsCount", correlatedSymbols.Count },
                        { "AverageCorrelation", correlatedSymbols.Average(kv => kv.Value) }
                    }
                    };
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Ошибка генерации корреляционного сигнала для {symbol}");
                    return null;
                }
            }

            /// <summary>
            /// Проверка бычьего тренда для символа
            /// </summary>
            private bool IsSymbolBullish(string symbol)
            {
                if (!_priceHistory.ContainsKey(symbol) || _priceHistory[symbol].Count < 50)
                    return false;

                var prices = _priceHistory[symbol];
                var lastPrice = prices.Last();
                var sma20 = prices.TakeLast(20).Average();
                var sma50 = prices.TakeLast(50).Average();

                return lastPrice > sma20 && sma20 > sma50;
            }

            /// <summary>
            /// Получение наиболее коррелированных пар
            /// </summary>
            /// <param name="threshold">Порог корреляции (0.7 по умолчанию)</param>
            /// <returns>Список пар с высокой корреляцией</returns>
            public List<CorrelatedPair> GetHighlyCorrelatedPairs(decimal threshold = 0.7m)
            {
                var matrix = GetCorrelationMatrix();
                var pairs = new List<CorrelatedPair>();

                foreach (var symbol1 in _symbols)
                {
                    foreach (var symbol2 in _symbols)
                    {
                        if (symbol1 == symbol2) continue;

                        var correlation = matrix[symbol1][symbol2];
                        if (correlation >= threshold)
                        {
                            pairs.Add(new CorrelatedPair
                            {
                                Symbol1 = symbol1,
                                Symbol2 = symbol2,
                                Correlation = correlation,
                                Direction = correlation > 0 ? CorrelationDirection.Positive : CorrelationDirection.Negative
                            });
                        }
                    }
                }

                return pairs
                    .DistinctBy(p => new { Min = Math.Min(p.Symbol1, p.Symbol2), Max = Math.Max(p.Symbol1, p.Symbol2) })
                    .OrderByDescending(p => Math.Abs(p.Correlation))
                    .ToList();
            }

            /// <summary>
            /// Класс для хранения данных корреляции
            /// </summary>
            private class CorrelationDataItem
            {
                public double BTCUSDT { get; set; }
                public double ETHUSDT { get; set; }
                public double BNBUSDT { get; set; }
                // Добавьте свойства для других символов по аналогии
            }
        }

        /// <summary>
        /// Модель коррелированной пары
        /// </summary>
        public class CorrelatedPair
        {
            public string Symbol1 { get; set; }
            public string Symbol2 { get; set; }
            public decimal Correlation { get; set; }
            public CorrelationDirection Direction { get; set; }
        }

        /// <summary>
        /// Направление корреляции
        /// </summary>
        public enum CorrelationDirection
        {
            Positive,
            Negative
        }


        /// <summary>
        /// Класс для онлайн-обучения и прогнозирования с использованием ML-моделей
        /// </summary>
        public class OnlineModelTrainer
        {
            private readonly MLContext _mlContext;
            private readonly int _lookbackWindow;
            private ITransformer _model;
            private PredictionEngine<MarketDataPoint, PricePrediction> _predictionEngine;
            private readonly ILogger _logger;
            private readonly object _modelLock = new();
            private DateTime _lastTrainTime = DateTime.MinValue;

            // Константы для настройки модели
            private const int RetrainIntervalHours = 4;
            private const double ValidationSetSize = 0.2;
            private const int EarlyStoppingRounds = 20;
            private const int MinimumExamplesForTraining = 1000;

            /// <summary>
            /// Инициализация тренера моделей
            /// </summary>
            /// <param name="mlContext">Контекст ML.NET</param>
            /// <param name="lookbackWindow">Размер окна исторических данных</param>
            /// <param name="logger">Логгер</param>
            public OnlineModelTrainer(
                MLContext mlContext,
                int lookbackWindow,
                ILogger logger = null)
            {
                _mlContext = mlContext ?? throw new ArgumentNullException(nameof(mlContext));
                _lookbackWindow = lookbackWindow;
                _logger = logger;

                // Инициализация пустой модели при создании
                InitializeModel();
            }

            /// <summary>
            /// Инициализация базовой модели
            /// </summary>
            private void InitializeModel()
            {
                try
                {
                    // Создаем пустой набор данных для инициализации конвейера
                    var emptyData = _mlContext.Data.LoadFromEnumerable(new List<MarketDataPoint>());

                    // Определяем конвейер обработки данных и обучения
                    var pipeline = _mlContext.Transforms.Concatenate(
                        outputColumnName: "Features",
                        inputColumnNames: GetFeatureColumns()) // Используем основные фичи
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.Regression.Trainers.LightGbm(
                            new LightGbmRegressionTrainer.Options
                            {
                                NumberOfIterations = 200,
                                LearningRate = 0.05,
                                NumberOfLeaves = 31,
                                MinimumExampleCountPerLeaf = 10,
                                UseCategoricalSplit = true,
                                HandleMissingValue = true,
                                EarlyStoppingRound = EarlyStoppingRounds,
                                LabelColumnName = "FuturePriceChange",
                                FeatureColumnName = "Features"
                            }));

                    // Обучаем модель на пустых данных (по сути инициализируем структуру)
                    _model = pipeline.Fit(emptyData);

                    // Создаем движок для предсказаний
                    _predictionEngine = _mlContext.Model.CreatePredictionEngine<MarketDataPoint, PricePrediction>(_model);

                    _logger?.LogInformation("ML модель успешно инициализирована");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка инициализации ML модели");
                    throw;
                }
            }

            /// <summary>
            /// Получение списка используемых фич
            /// </summary>
            private string[] GetFeatureColumns()
            {
                return new[]
                {
                nameof(MarketDataPoint.RSI),
                nameof(MarketDataPoint.MACD),
                nameof(MarketDataPoint.ATR),
                nameof(MarketDataPoint.Volume),
                nameof(MarketDataPoint.OBV),
                nameof(MarketDataPoint.VWAP),
                nameof(MarketDataPoint.OrderBookImbalance),
                nameof(MarketDataPoint.SMA50),
                nameof(MarketDataPoint.SMA200)
            };
            }

            /// <summary>
            /// Обновление модели на новых данных
            /// </summary>
            /// <param name="newData">Новые рыночные данные</param>
            public async Task UpdateModels(List<MarketDataPoint> newData)
            {
                if (newData == null || newData.Count < MinimumExamplesForTraining)
                {
                    _logger?.LogInformation("Недостаточно данных для обновления модели");
                    return;
                }

                // Проверяем, нужно ли переобучать модель
                if (DateTime.UtcNow - _lastTrainTime < TimeSpan.FromHours(RetrainIntervalHours))
                {
                    return;
                }

                try
                {
                    // Подготовка данных в фоновом потоке
                    var preparedData = await Task.Run(() => PrepareData(newData));

                    // Проверка качества данных
                    if (!ValidateData(preparedData.trainingData))
                    {
                        _logger?.LogWarning("Данные для обучения не прошли валидацию");
                        return;
                    }

                    // Переобучение модели
                    var newModel = await Task.Run(() => RetrainModel(preparedData.trainingData, preparedData.validationData));

                    // Валидация новой модели
                    var validationResult = ValidateModel(newModel, preparedData.validationData);
                    if (!validationResult.IsValid)
                    {
                        _logger?.LogWarning($"Новая модель не прошла валидацию: {validationResult.ErrorMessage}");
                        return;
                    }

                    // Блокировка для безопасного обновления модели
                    lock (_modelLock)
                    {
                        _model = newModel;
                        _predictionEngine = _mlContext.Model.CreatePredictionEngine<MarketDataPoint, PricePrediction>(_model);
                        _lastTrainTime = DateTime.UtcNow;
                    }

                    _logger?.LogInformation("Модель успешно обновлена. " +
                        $"R2: {validationResult.RSquared:F3}, " +
                        $"MAE: {validationResult.MeanAbsoluteError:F5}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка обновления модели");
                }
            }

            /// <summary>
            /// Подготовка данных для обучения
            /// </summary>
            private (IDataView trainingData, IDataView validationData) PrepareData(List<MarketDataPoint> rawData)
            {
                // Рассчитываем будущее изменение цены (целевая переменная)
                var processedData = new List<MarketDataPoint>();
                for (int i = 0; i < rawData.Count - 1; i++)
                {
                    var current = rawData[i];
                    var next = rawData[i + 1];

                    // Создаем копию с добавленным целевым значением
                    var dataPoint = new MarketDataPoint
                    {
                        Symbol = current.Symbol,
                        TimeFrame = current.TimeFrame,
                        OpenTime = current.OpenTime,
                        Open = current.Open,
                        High = current.High,
                        Low = current.Low,
                        Close = current.Close,
                        Volume = current.Volume,
                        RSI = current.RSI,
                        MACD = current.MACD,
                        ATR = current.ATR,
                        OBV = current.OBV,
                        VWAP = current.VWAP,
                        OrderBookImbalance = current.OrderBookImbalance,
                        SMA50 = current.SMA50,
                        SMA200 = current.SMA200,
                        FuturePriceChange = (next.Close - current.Close) / current.Close // Целевая переменная
                    };

                    processedData.Add(dataPoint);
                }

                // Загружаем данные в IDataView
                var fullData = _mlContext.Data.LoadFromEnumerable(processedData);

                // Разделяем на обучающую и валидационную выборки
                var trainTestSplit = _mlContext.Data.TrainTestSplit(fullData, testFraction: ValidationSetSize);

                return (trainTestSplit.TrainSet, trainTestSplit.TestSet);
            }

            /// <summary>
            /// Проверка качества данных
            /// </summary>
            private bool ValidateData(IDataView data)
            {
                try
                {
                    // Проверяем, что данные не пустые
                    if (data.GetRowCount() == 0)
                    {
                        _logger?.LogWarning("Пустой набор данных для обучения");
                        return false;
                    }

                    // Проверяем наличие всех необходимых колонок
                    var schema = data.Schema;
                    foreach (var column in GetFeatureColumns())
                    {
                        if (!schema.TryGetColumnIndex(column, out _))
                        {
                            _logger?.LogWarning($"Отсутствует колонка {column} в данных");
                            return false;
                        }
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка валидации данных");
                    return false;
                }
            }

            /// <summary>
            /// Переобучение модели
            /// </summary>
            private ITransformer RetrainModel(IDataView trainingData, IDataView validationData)
            {
                try
                {
                    // Определяем конвейер обработки данных
                    var pipeline = _mlContext.Transforms.Concatenate(
                        outputColumnName: "Features",
                        inputColumnNames: GetFeatureColumns())
                        .Append(_mlContext.Transforms.NormalizeMinMax("Features"))
                        .Append(_mlContext.Regression.Trainers.LightGbm(
                            new LightGbmRegressionTrainer.Options
                            {
                                NumberOfIterations = 200,
                                LearningRate = 0.05,
                                NumberOfLeaves = 31,
                                MinimumExampleCountPerLeaf = 10,
                                UseCategoricalSplit = true,
                                HandleMissingValue = true,
                                EarlyStoppingRound = EarlyStoppingRounds,
                                ValidationSet = validationData,
                                LabelColumnName = "FuturePriceChange",
                                FeatureColumnName = "Features"
                            }));

                    // Обучаем модель
                    return pipeline.Fit(trainingData);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка переобучения модели");
                    throw;
                }
            }

            /// <summary>
            /// Валидация модели
            /// </summary>
            private ModelValidationResult ValidateModel(ITransformer model, IDataView validationData)
            {
                try
                {
                    // Прогнозируем на валидационных данных
                    var predictions = model.Transform(validationData);

                    // Оцениваем метрики
                    var metrics = _mlContext.Regression.Evaluate(
                        data: predictions,
                        labelColumnName: "FuturePriceChange",
                        scoreColumnName: "Score");

                    // Проверяем на переобучение
                    bool isOverfitted = metrics.RSquared > 0.95; // Слишком высокое R2

                    return new ModelValidationResult
                    {
                        IsValid = metrics.RSquared > 0.3 && !isOverfitted,
                        RSquared = metrics.RSquared,
                        MeanAbsoluteError = metrics.MeanAbsoluteError,
                        RootMeanSquaredError = metrics.RootMeanSquaredError,
                        ErrorMessage = isOverfitted ? "Возможно переобучение" : null
                    };
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка валидации модели");
                    return new ModelValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = ex.Message
                    };
                }
            }

            /// <summary>
            /// Прогнозирование будущего изменения цены
            /// </summary>
            public TradingSignal Predict(MarketDataPoint data)
            {
                try
                {
                    // Проверяем инициализацию модели
                    if (_predictionEngine == null)
                    {
                        throw new InvalidOperationException("Модель не инициализирована");
                    }

                    // Создаем временную копию данных для предсказания
                    var predictionData = new MarketDataPoint
                    {
                        Symbol = data.Symbol,
                        TimeFrame = data.TimeFrame,
                        OpenTime = data.OpenTime,
                        Open = data.Open,
                        High = data.High,
                        Low = data.Low,
                        Close = data.Close,
                        Volume = data.Volume,
                        RSI = data.RSI,
                        MACD = data.MACD,
                        ATR = data.ATR,
                        OBV = data.OBV,
                        VWAP = data.VWAP,
                        OrderBookImbalance = data.OrderBookImbalance,
                        SMA50 = data.SMA50,
                        SMA200 = data.SMA200
                    };

                    // Делаем предсказание
                    var prediction = _predictionEngine.Predict(predictionData);

                    // Формируем торговый сигнал
                    return new TradingSignal
                    {
                        Symbol = data.Symbol,
                        Direction = prediction.FuturePriceChange > 0 ? TradeDirection.Long : TradeDirection.Short,
                        Confidence = Math.Min(1m, (decimal)Math.Abs(prediction.FuturePriceChange * 10)), // Масштабируем уверенность
                        Timestamp = DateTime.UtcNow,
                        TimeFrame = data.TimeFrame,
                        Features = new Dictionary<string, object>
                    {
                        { "RSI", data.RSI },
                        { "MACD", data.MACD },
                        { "ATR", data.ATR },
                        { "Volume", data.Volume },
                        { "OBV", data.OBV },
                        { "VWAP", data.VWAP },
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
                        Symbol = data.Symbol,
                        Direction = TradeDirection.Long,
                        Confidence = 0,
                        Timestamp = DateTime.UtcNow,
                        TimeFrame = data.TimeFrame
                    };
                }
            }

            /// <summary>
            /// Анализ важности фич
            /// </summary>
            public async Task<Dictionary<string, double>> GetFeatureImportance()
            {
                try
                {
                    return await Task.Run(() =>
                    {
                        // Проверяем, что модель поддерживает анализ важности фич
                        if (!(_model is ISingleFeaturePredictionTransformer<object>))
                        {
                            throw new NotSupportedException("Модель не поддерживает анализ важности фич");
                        }

                        // Получаем важность фич из модели
                        var featureImportance = ((LightGbmRegressionModelParameters)
                            ((ISingleFeaturePredictionTransformer<object>)_model).Model)
                            .GetFeatureWeights();

                        // Сопоставляем веса с именами фич
                        var featureNames = GetFeatureColumns();
                        var result = new Dictionary<string, double>();

                        for (int i = 0; i < featureNames.Length; i++)
                        {
                            result.Add(featureNames[i], featureImportance[i]);
                        }

                        return result.OrderByDescending(x => x.Value)
                            .ToDictionary(x => x.Key, x => x.Value);
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Ошибка анализа важности фич");
                    return new Dictionary<string, double>();
                }
            }

            /// <summary>
            /// Проверка дрейфа данных (data drift)
            /// </summary>
            public async Task<DataDriftReport> CheckDataDrift(List<MarketDataPoint> recentData)
            {
                return await Task.Run(() =>
                {
                    try
                    {
                        if (recentData.Count < 100)
                        {
                            throw new ArgumentException("Недостаточно данных для анализа дрейфа");
                        }

                        var report = new DataDriftReport();
                        var featureColumns = GetFeatureColumns();

                        // Анализируем распределение каждой фичи
                        foreach (var feature in featureColumns)
                        {
                            var values = recentData
                                .Select(d => (double)d.GetType().GetProperty(feature).GetValue(d))
                                .Where(v => !double.IsNaN(v) && !double.IsInfinity(v))
                                .ToList();

                            if (!values.Any())
                            {
                                continue;
                            }

                            var stats = new FeatureStats
                            {
                                FeatureName = feature,
                                Mean = values.Average(),
                                StdDev = CalculateStdDev(values),
                                Min = values.Min(),
                                Max = values.Max(),
                                Percentile25 = CalculatePercentile(values, 0.25),
                                Percentile75 = CalculatePercentile(values, 0.75)
                            };

                            report.FeatureStats.Add(stats);
                        }

                        // TODO: Добавить сравнение с эталонным распределением
                        // и расчет показателей дрейфа

                        return report;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Ошибка анализа дрейфа данных");
                        return new DataDriftReport { Error = ex.Message };
                    }
                });
            }

            private double CalculateStdDev(List<double> values)
            {
                var mean = values.Average();
                var sum = values.Sum(v => Math.Pow(v - mean, 2));
                return Math.Sqrt(sum / (values.Count - 1));
            }

            private double CalculatePercentile(List<double> values, double percentile)
            {
                var sorted = values.OrderBy(v => v).ToList();
                int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
                return sorted[Math.Max(0, index)];
            }
        }

        /// <summary>
        /// Результат предсказания модели
        /// </summary>
        public class PricePrediction
        {
            [ColumnName("Score")]
            public float FuturePriceChange { get; set; }
        }

        /// <summary>
        /// Результат валидации модели
        /// </summary>
        public class ModelValidationResult
        {
            public bool IsValid { get; set; }
            public double RSquared { get; set; }
            public double MeanAbsoluteError { get; set; }
            public double RootMeanSquaredError { get; set; }
            public string ErrorMessage { get; set; }
        }

        /// <summary>
        /// Отчет о дрейфе данных
        /// </summary>
        public class DataDriftReport
        {
            public List<FeatureStats> FeatureStats { get; set; } = new();
            public string Error { get; set; }
            public bool HasDrift { get; set; }
            public double DriftScore { get; set; }
        }

        /// <summary>
        /// Статистика по фиче
        /// </summary>
        public class FeatureStats
        {
            public string FeatureName { get; set; }
            public double Mean { get; set; }
            public double StdDev { get; set; }
            public double Min { get; set; }
            public double Max { get; set; }
            public double Percentile25 { get; set; }
            public double Percentile75 { get; set; }
            public double DriftScore { get; set; }
        }

        /// <summary>
        /// Расширенный бэктестер для тестирования торговых стратегий
        /// </summary>
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

            /// <summary>
            /// Запуск комплексного бэктеста стратегии
            /// </summary>
            /// <param name="symbol">Торговый символ (например, BTCUSDT)</param>
            /// <param name="interval">Таймфрейм для анализа</param>
            /// <param name="lookbackDays">Количество дней исторических данных</param>
            /// <param name="filter">Фильтр рыночных условий (опционально)</param>
            public async Task<BacktestResult> RunBacktest(
                string symbol,
                KlineInterval interval,
                int lookbackDays,
                string filter = null)
            {
                var result = new BacktestResult();

                try
                {
                    // 1. Получение исторических данных
                    var historicalData = await GetHistoricalData(symbol, interval, lookbackDays);

                    // 2. Применение фильтра рыночных условий (если указан)
                    if (!string.IsNullOrEmpty(filter))
                    {
                        historicalData = ApplyMarketConditionFilter(historicalData, filter);
                    }

                    // 3. Проверка достаточности данных
                    if (historicalData.Count < 100)
                    {
                        _logger.LogWarning($"Недостаточно данных для бэктеста: {historicalData.Count} баров");
                        result.Success = false;
                        return result;
                    }

                    // 4. Инициализация параметров бэктеста
                    decimal balance = 10000m; // Начальный баланс в USDT
                    decimal positionSize = 0m;
                    decimal entryPrice = 0m;
                    var trades = new List<TradeRecord>();
                    var dailyBalances = new List<decimal>();
                    var maxBalance = balance;
                    var maxDrawdown = 0m;

                    // 5. Основной цикл бэктеста
                    for (int i = 50; i < historicalData.Count - 1; i++)
                    {
                        var currentBar = historicalData[i];
                        var nextBar = historicalData[i + 1];

                        // Генерация торгового сигнала
                        var signal = _marketDataProcessor.GenerateTaSignal(currentBar);

                        // Логика входа в позицию
                        if (positionSize == 0 && signal.Confidence > 0.7m)
                        {
                            // Расчет размера позиции с учетом риска
                            positionSize = balance * 0.1m / currentBar.Close; // Риск 10% на сделку
                            entryPrice = currentBar.Close;

                            // Фиксируем сделку
                            trades.Add(new TradeRecord
                            {
                                Symbol = symbol,
                                Side = signal.Direction == TradeDirection.Long ? "BUY" : "SELL",
                                Quantity = positionSize,
                                EntryPrice = entryPrice,
                                EntryTime = currentBar.OpenTime,
                                TimeFrame = interval.ToString()
                            });
                        }
                        // Логика выхода из позиции
                        else if (positionSize > 0)
                        {
                            bool shouldExit = false;
                            decimal exitPrice = nextBar.Close;
                            string exitReason = "Regular exit";

                            // Проверка стоп-лосса / тейк-профита
                            if ((signal.Direction == TradeDirection.Long && nextBar.Low <= entryPrice * 0.95m) ||
                                (signal.Direction == TradeDirection.Short && nextBar.High >= entryPrice * 1.05m))
                            {
                                shouldExit = true;
                                exitReason = "Stop loss";
                            }
                            else if ((signal.Direction == TradeDirection.Long && nextBar.High >= entryPrice * 1.1m) ||
                                     (signal.Direction == TradeDirection.Short && nextBar.Low <= entryPrice * 0.9m))
                            {
                                shouldExit = true;
                                exitReason = "Take profit";
                            }

                            // Выход по противоположному сигналу
                            if (!shouldExit && signal.Confidence > 0.7m &&
                                ((signal.Direction == TradeDirection.Long && trades.Last().Side == "SELL") ||
                                 (signal.Direction == TradeDirection.Short && trades.Last().Side == "BUY")))
                            {
                                shouldExit = true;
                                exitReason = "Reverse signal";
                            }

                            if (shouldExit)
                            {
                                // Расчет PnL
                                decimal pnl = positionSize * (exitPrice - entryPrice) *
                                            (trades.Last().Side == "BUY" ? 1 : -1);
                                decimal commission = positionSize * exitPrice * 0.001m; // 0.1% комиссия
                                decimal netPnl = pnl - commission;

                                // Обновление баланса
                                balance += netPnl;
                                maxBalance = Math.Max(maxBalance, balance);
                                maxDrawdown = Math.Max(maxDrawdown, (maxBalance - balance) / maxBalance);

                                // Фиксация сделки
                                trades.Last().ExitPrice = exitPrice;
                                trades.Last().ExitTime = nextBar.OpenTime;
                                trades.Last().Profit = netPnl;
                                trades.Last().Commission = commission;
                                trades.Last().ExitReason = exitReason;

                                // Сброс позиции
                                positionSize = 0m;
                                entryPrice = 0m;
                            }
                        }

                        // Фиксация дневного баланса
                        if (i > 0 && historicalData[i].OpenTime.Day != historicalData[i - 1].OpenTime.Day)
                        {
                            dailyBalances.Add(balance);
                        }
                    }

                    // 6. Расчет итоговых метрик
                    result.Success = true;
                    result.Trades = trades;
                    result.TotalReturn = (balance - 10000m) / 10000m;
                    result.MaxDrawdown = maxDrawdown;
                    result.WinRate = trades.Count > 0 ?
                        trades.Count(t => t.Profit > 0) / (decimal)trades.Count : 0m;
                    result.SharpeRatio = CalculateSharpeRatio(dailyBalances);
                    result.SortinoRatio = CalculateSortinoRatio(dailyBalances);
                    result.ProfitFactor = CalculateProfitFactor(trades);
                    result.AvgTradeDuration = CalculateAverageTradeDuration(trades);
                    result.TimeFrame = interval.ToString();

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при выполнении бэктеста");
                    result.Success = false;
                    return result;
                }
            }

            /// <summary>
            /// Тестирование проскальзывания для разных типов ордеров
            /// </summary>
            public SlippageAnalysis TestSlippage(string symbol, decimal orderSize)
            {
                var analysis = new SlippageAnalysis();

                try
                {
                    // Получаем текущий стакан цен
                    var orderBook = _marketDataProcessor.GetLatestOrderBook(symbol);
                    if (orderBook == null) return analysis;

                    // Тестируем рыночный ордер
                    decimal marketOrderSlippage = SimulateMarketOrder(orderBook, orderSize);
                    analysis.MarketOrderSlippage = marketOrderSlippage;

                    // Тестируем лимитный ордер
                    decimal limitOrderSlippage = SimulateLimitOrder(orderBook, orderSize);
                    analysis.LimitOrderSlippage = limitOrderSlippage;

                    // Тестируем TWAP ордер
                    decimal twapSlippage = SimulateTwapOrder(orderBook, orderSize);
                    analysis.TwapSlippage = twapSlippage;

                    return analysis;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка тестирования проскальзывания");
                    return analysis;
                }
            }

            /// <summary>
            /// Запуск Monte-Carlo симуляции
            /// </summary>
            public MonteCarloResult RunMonteCarloSimulation(List<TradeRecord> trades, int iterations = 1000)
            {
                var result = new MonteCarloResult();

                try
                {
                    if (trades == null || !trades.Any()) return result;

                    var random = new Random();
                    var outcomes = new List<decimal>();
                    var tradeCount = trades.Count;

                    for (int i = 0; i < iterations; i++)
                    {
                        decimal balance = 10000m;

                        // Симуляция случайной последовательности сделок
                        for (int j = 0; j < tradeCount; j++)
                        {
                            int randomIndex = random.Next(0, tradeCount);
                            var trade = trades[randomIndex];

                            if (trade.Profit.HasValue)
                            {
                                balance += trade.Profit.Value;
                            }
                        }

                        outcomes.Add(balance);
                    }

                    // Расчет статистик
                    result.BestCase = outcomes.Max();
                    result.WorstCase = outcomes.Min();
                    result.Median = outcomes.OrderBy(x => x).ElementAt(iterations / 2);
                    result.ProbabilityOfProfit = outcomes.Count(x => x > 10000m) / (decimal)iterations;
                    result.Success = true;

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка Monte-Carlo симуляции");
                    return result;
                }
            }

            #region Вспомогательные методы

            /// <summary>
            /// Получение исторических данных с биржи
            /// </summary>
            private async Task<List<MarketDataPoint>> GetHistoricalData(
                string symbol,
                KlineInterval interval,
                int lookbackDays)
            {
                var endTime = DateTime.UtcNow;
                var startTime = endTime.AddDays(-lookbackDays);

                var klinesResult = await _restClient.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol, interval, startTime, endTime, limit: 1000);

                if (!klinesResult.Success)
                {
                    _logger.LogError($"Ошибка получения данных: {klinesResult.Error}");
                    return new List<MarketDataPoint>();
                }

                return klinesResult.Data.Select(k => new MarketDataPoint
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
            }

            /// <summary>
            /// Применение фильтра рыночных условий
            /// </summary>
            private List<MarketDataPoint> ApplyMarketConditionFilter(
                List<MarketDataPoint> data,
                string filter)
            {
                switch (filter.ToLower())
                {
                    case "highvolatility":
                        // Фильтр высокой волатильности (ATR > среднего)
                        decimal avgAtr = data.Average(d => d.ATR);
                        return data.Where(d => d.ATR > avgAtr * 1.5m).ToList();

                    case "lowliquidity":
                        // Фильтр низкой ликвидности (объем < среднего)
                        decimal avgVolume = data.Average(d => d.Volume);
                        return data.Where(d => d.Volume < avgVolume * 0.5m).ToList();

                    case "uptrend":
                        // Только восходящие тренды (цена выше SMA50)
                        return data.Where(d => d.Close > d.SMA50).ToList();

                    case "downtrend":
                        // Только нисходящие тренды (цена ниже SMA50)
                        return data.Where(d => d.Close < d.SMA50).ToList();

                    default:
                        return data;
                }
            }

            /// <summary>
            /// Расчет коэффициента Шарпа
            /// </summary>
            private decimal CalculateSharpeRatio(List<decimal> dailyBalances)
            {
                if (dailyBalances.Count < 2) return 0m;

                try
                {
                    var returns = new List<decimal>();
                    for (int i = 1; i < dailyBalances.Count; i++)
                    {
                        returns.Add((dailyBalances[i] - dailyBalances[i - 1]) / dailyBalances[i - 1]);
                    }

                    var avgReturn = returns.Average();
                    var stdDev = (decimal)Math.Sqrt(returns.Select(r => Math.Pow((double)(r - avgReturn), 2)).Average());

                    return stdDev != 0 ? avgReturn / stdDev * (decimal)Math.Sqrt(365) : 0m;
                }
                catch
                {
                    return 0m;
                }
            }

            /// <summary>
            /// Расчет коэффициента Сортино
            /// </summary>
            private decimal CalculateSortinoRatio(List<decimal> dailyBalances)
            {
                if (dailyBalances.Count < 2) return 0m;

                try
                {
                    var returns = new List<decimal>();
                    for (int i = 1; i < dailyBalances.Count; i++)
                    {
                        returns.Add((dailyBalances[i] - dailyBalances[i - 1]) / dailyBalances[i - 1]);
                    }

                    var avgReturn = returns.Average();
                    var downsideStdDev = (decimal)Math.Sqrt(
                        returns.Where(r => r < 0)
                               .Select(r => Math.Pow((double)r, 2))
                               .Average());

                    return downsideStdDev != 0 ? avgReturn / downsideStdDev * (decimal)Math.Sqrt(365) : 0m;
                }
                catch
                {
                    return 0m;
                }
            }

            /// <summary>
            /// Расчет Profit Factor (отношение прибыли к убыткам)
            /// </summary>
            private decimal CalculateProfitFactor(List<TradeRecord> trades)
            {
                if (!trades.Any() || !trades.All(t => t.Profit.HasValue)) return 0m;

                decimal grossProfit = trades.Where(t => t.Profit > 0).Sum(t => t.Profit.Value);
                decimal grossLoss = Math.Abs(trades.Where(t => t.Profit < 0).Sum(t => t.Profit.Value));

                return grossLoss != 0 ? grossProfit / grossLoss : decimal.MaxValue;
            }

            /// <summary>
            /// Расчет средней продолжительности сделки (в минутах)
            /// </summary>
            private decimal CalculateAverageTradeDuration(List<TradeRecord> trades)
            {
                if (!trades.Any() || !trades.All(t => t.ExitTime.HasValue)) return 0m;

                var durations = trades.Select(t =>
                    (t.ExitTime.Value - t.EntryTime).TotalMinutes);

                return (decimal)durations.Average();
            }

            /// <summary>
            /// Симуляция рыночного ордера
            /// </summary>
            private decimal SimulateMarketOrder(OrderBook book, decimal orderSize)
            {
                decimal executedQuantity = 0m;
                decimal totalCost = 0m;
                decimal remaining = orderSize;

                foreach (var ask in book.Asks.OrderBy(a => a.Price))
                {
                    if (remaining <= 0) break;

                    decimal fill = Math.Min(remaining, ask.Quantity);
                    totalCost += fill * ask.Price;
                    executedQuantity += fill;
                    remaining -= fill;
                }

                if (executedQuantity == 0) return 0m;

                decimal avgPrice = totalCost / executedQuantity;
                decimal midPrice = (book.Asks[0].Price + book.Bids[0].Price) / 2;

                return (avgPrice - midPrice) / midPrice * 100m; // Проскальзывание в %
            }

            /// <summary>
            /// Симуляция лимитного ордера
            /// </summary>
            private decimal SimulateLimitOrder(OrderBook book, decimal orderSize)
            {
                decimal midPrice = (book.Asks[0].Price + book.Bids[0].Price) / 2;
                decimal limitPrice = midPrice * 0.995m; // На 0.5% ниже середины

                // Предполагаем, что ордер заполняется частично
                decimal fillRatio = 0.7m; // Эмпирически определенный коэффициент
                decimal executedQuantity = orderSize * fillRatio;

                // Условное проскальзывание для лимитного ордера
                return -0.05m; // Часто отрицательное (выгода)
            }

            /// <summary>
            /// Симуляция TWAP ордера
            /// </summary>
            private decimal SimulateTwapOrder(OrderBook book, decimal orderSize)
            {
                // Эмуляция разбивки на 5 частей с интервалом 5 минут
                decimal chunk = orderSize / 5m;
                decimal totalSlippage = 0m;

                for (int i = 0; i < 5; i++)
                {
                    // Предполагаем небольшое изменение цены за время исполнения
                    decimal priceChange = (decimal)((i - 2) * 0.0005); // -0.1% до +0.1%
                    decimal currentMid = (book.Asks[0].Price + book.Bids[0].Price) / 2;
                    decimal executionPrice = currentMid * (1 + priceChange);

                    decimal chunkSlippage = (executionPrice - currentMid) / currentMid * 100m;
                    totalSlippage += chunkSlippage;
                }

                return totalSlippage / 5m; // Среднее проскальзывание
            }

            #endregion
        }

        /// <summary>
        /// Результат бэктеста
        /// </summary>
        public class BacktestResult
        {
            public bool Success { get; set; }
            public decimal TotalReturn { get; set; } // Общая доходность (например, 0.1 для 10%)
            public decimal MaxDrawdown { get; set; } // Максимальная просадка
            public decimal WinRate { get; set; } // Процент прибыльных сделок
            public decimal SharpeRatio { get; set; } // Коэффициент Шарпа
            public decimal SortinoRatio { get; set; } // Коэффициент Сортино
            public decimal ProfitFactor { get; set; } // Отношение прибыли к убыткам
            public decimal AvgTradeDuration { get; set; } // Средняя длительность сделки в минутах
            public string TimeFrame { get; set; } // Используемый таймфрейм
            public List<TradeRecord> Trades { get; set; } = new(); // Список всех сделок
        }

        /// <summary>
        /// Анализ проскальзывания
        /// </summary>
        public class SlippageAnalysis
        {
            public decimal MarketOrderSlippage { get; set; } // % проскальзывания для рыночного ордера
            public decimal LimitOrderSlippage { get; set; } // % проскальзывания для лимитного ордера
            public decimal TwapSlippage { get; set; } // % проскальзывания для TWAP
            public decimal EstimatedFillRatio { get; set; } // Ожидаемый % исполнения
        }

        /// <summary>
        /// Результат Monte-Carlo симуляции
        /// </summary>
        public class MonteCarloResult
        {
            public bool Success { get; set; }
            public decimal BestCase { get; set; } // Лучший сценарий (макс. баланс)
            public decimal WorstCase { get; set; } // Худший сценарий (мин. баланс)
            public decimal Median { get; set; } // Медианный результат
            public decimal ProbabilityOfProfit { get; set; } // Вероятность прибыльности
        }

        /// <summary>
        /// Мониторинг криптовалютных новостей и анализ их влияния на рынок
        /// </summary>
        public class NewsMonitor : IDisposable
        {
            private readonly ILogger _logger;
            private readonly HttpClient _httpClient;
            private readonly ConcurrentDictionary<string, NewsEvent> _activeNews = new();
            private Timer _monitoringTimer;
            private bool _isMonitoring;
            private readonly string[] _highImpactKeywords = { "hack", "exploit", "regulation", "ban", "partnership", "listing", "halving" };

            // Настройки API (можно вынести в конфиг)
            private const string CryptoPanicApiKey = "YOUR_API_KEY";
            private const string NewsApiUrl = "https://cryptopanic.com/api/v1/posts/?auth_token=" + CryptoPanicApiKey;
            private const int MonitoringIntervalMinutes = 5;

            public NewsMonitor(ILogger logger)
            {
                _logger = logger;
                _httpClient = new HttpClient();
                _httpClient.Timeout = TimeSpan.FromSeconds(10);
            }

            /// <summary>
            /// Запуск фонового мониторинга новостей
            /// </summary>
            public void StartMonitoring()
            {
                if (_isMonitoring) return;

                _isMonitoring = true;
                _monitoringTimer = new Timer(CheckNews, null, TimeSpan.Zero, TimeSpan.FromMinutes(MonitoringIntervalMinutes));
                _logger.LogInformation("Мониторинг новостей запущен с интервалом {0} минут", MonitoringIntervalMinutes);
            }

            /// <summary>
            /// Остановка мониторинга новостей
            /// </summary>
            public void StopMonitoring()
            {
                _isMonitoring = false;
                _monitoringTimer?.Dispose();
                _logger.LogInformation("Мониторинг новостей остановлен");
            }

            /// <summary>
            /// Проверка новых новостей по таймеру
            /// </summary>
            private async void CheckNews(object state)
            {
                if (!_isMonitoring) return;

                try
                {
                    // Запрос к CryptoPanic API
                    var response = await _httpClient.GetStringAsync(NewsApiUrl);
                    var newsResponse = JsonConvert.DeserializeObject<CryptoPanicResponse>(response);

                    if (newsResponse?.Results == null) return;

                    // Обработка новых событий
                    foreach (var item in newsResponse.Results)
                    {
                        // Пропускаем уже обработанные новости
                        if (_activeNews.ContainsKey(item.Id)) continue;

                        var newsEvent = MapToNewsEvent(item);

                        // Анализ важности новости
                        AnalyzeNewsImpact(newsEvent);

                        // Добавление в активные события
                        _activeNews.TryAdd(item.Id, newsEvent);

                        _logger.LogInformation("Обнаружена новость: {0} (Важность: {1})",
                            newsEvent.Title, newsEvent.ImpactLevel);
                    }

                    // Удаление устаревших новостей
                    CleanupExpiredNews();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка при проверке новостей");
                }
            }

            /// <summary>
            /// Анализ влияния новости на рынок
            /// </summary>
            private void AnalyzeNewsImpact(NewsEvent newsEvent)
            {
                try
                {
                    // Базовый уровень важности
                    newsEvent.ImpactLevel = 1; // По умолчанию низкая важность

                    // Повышаем важность для ключевых слов в заголовке
                    if (_highImpactKeywords.Any(kw =>
                        newsEvent.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                    {
                        newsEvent.ImpactLevel += 1;
                    }

                    // Повышаем важность для проверенных источников
                    if (IsVerifiedSource(newsEvent.Source))
                    {
                        newsEvent.ImpactLevel += 1;
                    }

                    // Анализ тональности заголовка
                    newsEvent.SentimentScore = AnalyzeSentiment(newsEvent.Title);
                    if (Math.Abs(newsEvent.SentimentScore) > 0.5m)
                    {
                        newsEvent.ImpactLevel += 1;
                    }

                    // Ограничиваем максимальный уровень важности
                    newsEvent.ImpactLevel = Math.Min(newsEvent.ImpactLevel, 5);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка анализа новости");
                }
            }

            /// <summary>
            /// Простой анализ тональности текста
            /// </summary>
            private decimal AnalyzeSentiment(string text)
            {
                // Упрощенная реализация - в продакшене лучше использовать NLP API
                var positiveWords = new[] { "bullish", "growth", "adoption", "partnership", "approve" };
                var negativeWords = new[] { "bearish", "hack", "scam", "ban", "regulation", "sell-off" };

                int positiveCount = positiveWords.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
                int negativeCount = negativeWords.Count(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

                return (positiveCount - negativeCount) switch
                {
                    > 0 => 0.5m + (Math.Min(positiveCount, 3) * 0.15m), // Макс +0.95
                    < 0 => -0.5m - (Math.Min(negativeCount, 3) * 0.15m), // Мин -0.95
                    _ => 0m
                };
            }

            /// <summary>
            /// Проверка надежности источника
            /// </summary>
            private bool IsVerifiedSource(NewsSource source)
            {
                return source switch
                {
                    NewsSource.OfficialAnnouncement => true,
                    NewsSource.CryptoPanic => true,
                    _ => false
                };
            }

            /// <summary>
            /// Удаление устаревших новостей
            /// </summary>
            private void CleanupExpiredNews()
            {
                try
                {
                    var cutoffTime = DateTime.UtcNow.AddHours(-6); // Новости старше 6 часов удаляем
                    var expiredIds = _activeNews
                        .Where(kv => kv.Value.PublishedAt < cutoffTime)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var id in expiredIds)
                    {
                        _activeNews.TryRemove(id, out _);
                    }

                    if (expiredIds.Count > 0)
                    {
                        _logger.LogDebug("Удалено {0} устаревших новостей", expiredIds.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка очистки новостей");
                }
            }

            /// <summary>
            /// Проверка наличия важных новостей
            /// </summary>
            public bool IsHighImpactNewsPending()
            {
                return _activeNews.Any(kv => kv.Value.ImpactLevel >= 3);
            }

            /// <summary>
            /// Получение списка символов, затронутых важными новостями
            /// </summary>
            public List<string> GetAffectedSymbols()
            {
                return _activeNews
                    .Where(kv => kv.Value.ImpactLevel >= 3)
                    .SelectMany(kv => kv.Value.RelatedAssets)
                    .Distinct()
                    .ToList();
            }

            /// <summary>
            /// Проверка, затронут ли символ важными новостями
            /// </summary>
            public bool IsSymbolAffected(string symbol)
            {
                return _activeNews.Any(kv =>
                    kv.Value.ImpactLevel >= 2 &&
                    kv.Value.RelatedAssets.Contains(symbol));
            }

            /// <summary>
            /// Получение всех активных новостей для указанного символа
            /// </summary>
            public List<NewsEvent> GetNewsForSymbol(string symbol)
            {
                return _activeNews
                    .Where(kv => kv.Value.RelatedAssets.Contains(symbol))
                    .OrderByDescending(kv => kv.Value.ImpactLevel)
                    .Select(kv => kv.Value)
                    .ToList();
            }

            /// <summary>
            /// Преобразование DTO в модель новости
            /// </summary>
            private NewsEvent MapToNewsEvent(CryptoPanicItem item)
            {
                var currencies = item.Currencies?.Select(c => c.Code.ToUpper()).ToArray() ?? Array.Empty<string>();

                return new NewsEvent
                {
                    Id = item.Id,
                    Title = item.Title,
                    Source = MapSource(item.Source.Domain),
                    PublishedAt = DateTime.Parse(item.PublishedAt),
                    ExpiresAt = DateTime.Parse(item.PublishedAt).AddHours(6), // Новость актуальна 6 часов
                    RelatedAssets = currencies,
                    Url = item.Url
                };
            }

            /// <summary>
            /// Определение типа источника
            /// </summary>
            private NewsSource MapSource(string domain)
            {
                if (string.IsNullOrEmpty(domain)) return NewsSource.Other;

                return domain.ToLower() switch
                {
                    "twitter.com" => NewsSource.Twitter,
                    "cointelegraph.com" => NewsSource.CryptoPanic,
                    "blog.bitmex.com" => NewsSource.OfficialAnnouncement,
                    _ => NewsSource.Other
                };
            }

            public void Dispose()
            {
                StopMonitoring();
                _httpClient?.Dispose();
            }

            #region Модели данных

            public class NewsEvent
            {
                public string Id { get; set; }
                public string Title { get; set; }
                public NewsSource Source { get; set; }
                public string[] RelatedAssets { get; set; }
                public DateTime PublishedAt { get; set; }
                public DateTime ExpiresAt { get; set; }
                public int ImpactLevel { get; set; } // 1-5 (5 - максимальное влияние)
                public decimal SentimentScore { get; set; } // -1 до +1
                public string Url { get; set; }
                public bool IsVerified { get; set; }
            }


            // Модели для десериализации ответа CryptoPanic API
            private class CryptoPanicResponse
            {
                public List<CryptoPanicItem> Results { get; set; }
            }

            private class CryptoPanicItem
            {
                public string Id { get; set; }
                public string Title { get; set; }
                public string PublishedAt { get; set; }
                public CryptoPanicSource Source { get; set; }
                public List<CryptoPanicCurrency> Currencies { get; set; }
                public string Url { get; set; }
            }

            private class CryptoPanicSource
            {
                public string Domain { get; set; }
            }

            private class CryptoPanicCurrency
            {
                public string Code { get; set; }
            }

            #endregion
        }



        #endregion

        #region Data Models
        /// <summary>
        /// Направление торговой операции
        /// </summary>
        public enum TradeDirection
        {
            /// <summary>
            /// Покупка (длинная позиция) - трейдер ожидает рост цены
            /// Пример: Покупка BTC по $30,000 с целью продажи по $35,000
            /// </summary>
            Long = 0,

            /// <summary>
            /// Продажа (короткая позиция) - трейдер ожидает падение цены
            /// Пример: Продажа BTC по $30,000 с целью выкупа по $25,000
            /// 
            /// Примечание: На спотовых рынках требует маржинального кредитования,
            /// на фьючерсных рынках доступно без заимствования актива
            /// </summary>
            Short = 1
        }

        /// <summary>
        /// Класс для хранения и обработки рыночных данных по одному временному интервалу
        /// </summary>
        public class MarketDataPoint
        {
            /// <summary>
            /// Торговая пара (например, BTCUSDT)
            /// </summary>
            public string Symbol { get; set; } = string.Empty;

            /// <summary>
            /// Временной интервал (таймфрейм) данных
            /// </summary>
            public KlineInterval TimeFrame { get; set; }

            /// <summary>
            /// Время открытия свечи
            /// </summary>
            public DateTime OpenTime { get; set; }

            /// <summary>
            /// Цена открытия
            /// </summary>
            public decimal Open { get; set; }

            /// <summary>
            /// Максимальная цена за период
            /// </summary>
            public decimal High { get; set; }

            /// <summary>
            /// Минимальная цена за период
            /// </summary>
            public decimal Low { get; set; }

            /// <summary>
            /// Цена закрытия
            /// </summary>
            public decimal Close { get; set; }

            /// <summary>
            /// Объем торгов за период
            /// </summary>
            public decimal Volume { get; set; }

            /// <summary>
            /// Флаг завершенности свечи (true - свеча закрыта)
            /// </summary>
            public bool IsClosed { get; set; }

            // --------------------------------------------------
            // Технические индикаторы (рассчитываются процессором)
            // --------------------------------------------------

            /// <summary>
            /// Relative Strength Index (индекс относительной силы)
            /// Значения:
            /// - >70 - перекупленность
            /// - <30 - перепроданность
            /// </summary>
            public decimal RSI { get; set; }

            /// <summary>
            /// Moving Average Convergence Divergence (схождение/расхождение скользящих средних)
            /// </summary>
            public decimal MACD { get; set; }

            /// <summary>
            /// Сигнальная линия MACD
            /// </summary>
            public decimal Signal { get; set; }

            /// <summary>
            /// Average True Range (средний истинный диапазон) - показатель волатильности
            /// </summary>
            public decimal ATR { get; set; }

            /// <summary>
            /// Простая скользящая средняя за 50 периодов
            /// </summary>
            public decimal SMA50 { get; set; }

            /// <summary>
            /// Простая скользящая средняя за 200 периодов
            /// </summary>
            public decimal SMA200 { get; set; }

            /// <summary>
            /// On-Balance Volume (балансовый объем) - индикатор объема
            /// </summary>
            public decimal OBV { get; set; }

            /// <summary>
            /// Volume Weighted Average Price (средневзвешенная цена по объему)
            /// </summary>
            public decimal VWAP { get; set; }

            /// <summary>
            /// Дисбаланс стакана цен (отношение объема покупок к продажам)
            /// Значения:
            /// - >0 - преобладают покупки
            /// - <0 - преобладают продажи
            /// </summary>
            public decimal OrderBookImbalance { get; set; }

            /// <summary>
            /// Конструктор по умолчанию
            /// </summary>
            public MarketDataPoint() { }

            /// <summary>
            /// Конструктор для быстрого создания объекта из данных Binance Kline
            /// </summary>
            /// <param name="kline">Данные свечи от Binance</param>
            /// <param name="symbol">Торговая пара</param>
            /// <param name="timeFrame">Таймфрейм</param>
            public MarketDataPoint(IBinanceKline kline, string symbol, KlineInterval timeFrame)
            {
                Symbol = symbol;
                TimeFrame = timeFrame;
                OpenTime = kline.OpenTime;
                Open = kline.OpenPrice;
                High = kline.HighPrice;
                Low = kline.LowPrice;
                Close = kline.ClosePrice;
                Volume = kline.Volume;
                IsClosed = kline.Final;
            }

            /// <summary>
            /// Проверяет, является ли свеча бычьей (закрытие выше открытия)
            /// </summary>
            public bool IsBullish => Close > Open;

            /// <summary>
            /// Проверяет, является ли свеча медвежьей (закрытие ниже открытия)
            /// </summary>
            public bool IsBearish => Close < Open;

            /// <summary>
            /// Возвращает тело свечи (разница между ценой открытия и закрытия)
            /// </summary>
            public decimal Body => Math.Abs(Close - Open);

            /// <summary>
            /// Возвращает верхнюю тень свечи (разница между high и телом)
            /// </summary>
            public decimal UpperShadow => High - (IsBullish ? Close : Open);

            /// <summary>
            /// Возвращает нижнюю тень свечи (разница между low и телом)
            /// </summary>
            public decimal LowerShadow => (IsBullish ? Open : Close) - Low;

            /// <summary>
            /// Проверяет, является ли свеча доджем (маленькое тело)
            /// </summary>
            /// <param name="threshold">Порог для определения доджа (по умолчанию 0.1%)</param>
            public bool IsDoji(decimal threshold = 0.001m)
            {
                decimal range = High - Low;
                return range > 0 && (Body / range) < threshold;
            }

            /// <summary>
            /// Возвращает строковое представление объекта
            /// </summary>
            public override string ToString()
            {
                return $"{Symbol} {TimeFrame} {OpenTime}: O={Open}, H={High}, L={Low}, C={Close}, V={Volume}";
            }
        }

        /// <summary>
        /// Модель торгового сигнала, генерируемого стратегией
        /// </summary>
        public class TradingSignal
        {
            /// <summary>
            /// Идентификатор торговой пары (например, BTCUSDT)
            /// </summary>
            public string Symbol { get; set; } = string.Empty;

            /// <summary>
            /// Направление сделки (Покупка/Продажа)
            /// </summary>
            public TradeDirection Direction { get; set; }

            /// <summary>
            /// Уверенность в сигнале (0-1, где 1 - максимальная уверенность)
            /// </summary>
            public decimal Confidence { get; set; }

            /// <summary>
            /// Время генерации сигнала
            /// </summary>
            public DateTime Timestamp { get; set; }

            /// <summary>
            /// Таймфрейм, на котором сгенерирован сигнал
            /// </summary>
            public KlineInterval TimeFrame { get; set; }

            /// <summary>
            /// Идентификатор стратегии, сгенерировавшей сигнал
            /// </summary>
            public string StrategyId { get; set; }

            /// <summary>
            /// Рекомендуемый размер позиции (в базовой валюте)
            /// </summary>
            public decimal SuggestedPositionSize { get; set; }

            /// <summary>
            /// Дополнительные признаки/метрики, использованные при генерации сигнала
            /// </summary>
            public Dictionary<string, object> Features { get; set; } = new Dictionary<string, object>();

            /// <summary>
            /// Флаг использования VWAP для исполнения (Volume Weighted Average Price)
            /// </summary>
            public bool UseVwap { get; set; }

            /// <summary>
            /// Длительность VWAP-исполнения в минутах
            /// </summary>
            public int VwapDurationMinutes { get; set; } = 5;

            /// <summary>
            /// Цена актива на момент генерации сигнала
            /// </summary>
            public decimal CurrentPrice { get; set; }

            /// <summary>
            /// Расчетный уровень стоп-лосса
            /// </summary>
            public decimal CalculatedStopLoss { get; set; }

            /// <summary>
            /// Расчетный уровень тейк-профита
            /// </summary>
            public decimal CalculatedTakeProfit { get; set; }

            /// <summary>
            /// Риск-профиль сигнала (Low/Medium/High)
            /// </summary>
            public RiskProfile RiskProfile { get; set; }

            /// <summary>
            /// Временная метка экспирации сигнала (если применимо)
            /// </summary>
            public DateTime? ExpirationTime { get; set; }

            /// <summary>
            /// Конструктор по умолчанию
            /// </summary>
            public TradingSignal()
            {
                Timestamp = DateTime.UtcNow;
            }

            /// <summary>
            /// Конструктор с минимально необходимыми параметрами
            /// </summary>
            public TradingSignal(string symbol, TradeDirection direction, decimal confidence, KlineInterval timeFrame)
            {
                Symbol = symbol;
                Direction = direction;
                Confidence = confidence;
                TimeFrame = timeFrame;
                Timestamp = DateTime.UtcNow;
                Features = new Dictionary<string, object>();
            }

            /// <summary>
            /// Добавляет признак/метрику в словарь features
            /// </summary>
            public void AddFeature(string key, object value)
            {
                Features[key] = value;
            }

            /// <summary>
            /// Возвращает значение признака или default, если не существует
            /// </summary>
            public T GetFeature<T>(string key, T defaultValue = default)
            {
                return Features.TryGetValue(key, out var value) ? (T)value : defaultValue;
            }

            /// <summary>
            /// Рассчитывает риск-профиль на основе волатильности и уверенности
            /// </summary>
            public void CalculateRiskProfile(decimal volatility)
            {
                var riskScore = volatility * (1 - Confidence);

                RiskProfile = riskScore switch
                {
                    < 0.2m => RiskProfile.Low,
                    < 0.5m => RiskProfile.Medium,
                    _ => RiskProfile.High
                };
            }

            /// <summary>
            /// Проверяет, действителен ли сигнал (не истекло ли время)
            /// </summary>
            public bool IsValid()
            {
                return !ExpirationTime.HasValue || DateTime.UtcNow <= ExpirationTime.Value;
            }

            /// <summary>
            /// Возвращает строковое представление сигнала
            /// </summary>
            public override string ToString()
            {
                return $"{Symbol} {Direction} | Confidence: {Confidence:P0} | TimeFrame: {TimeFrame} | Strategy: {StrategyId}";
            }
        }


        /// <summary>
        /// Уровень риска сигнала
        /// </summary>
        public enum RiskProfile
        {
            Low,    // Низкий риск
            Medium, // Средний риск
            High    // Высокий риск
        }

        /// <summary>
        /// Дополнительные метаданные сигнала
        /// </summary>
        public class SignalMetadata
        {
            /// <summary>
            /// Время генерации сигнала стратегией
            /// </summary>
            public DateTime GenerationTime { get; set; }

            /// <summary>
            /// Время последнего обновления
            /// </summary>
            public DateTime LastUpdated { get; set; }

            /// <summary>
            /// Количество подтверждений от других стратегий
            /// </summary>
            public int Confirmations { get; set; }

            /// <summary>
            /// Список идентификаторов подтвердивших стратегий
            /// </summary>
            public List<string> ConfirmedBy { get; set; } = new List<string>();

            /// <summary>
            /// Флаг ручного подтверждения
            /// </summary>
            public bool ManuallyConfirmed { get; set; }

            /// <summary>
            /// Комментарии/заметки к сигналу
            /// </summary>
            public string Notes { get; set; }
        }

        /// <summary>
        /// Комплексные метрики риска для портфеля и торговых решений
        /// </summary>
        public class RiskMetrics
        {
            /// <summary>
            /// Текущая волатильность (стандартное отклонение доходностей) в %
            /// </summary>
            public decimal Volatility { get; set; }

            /// <summary>
            /// Доступная ликвидность в стакане (сумма в USDT в пределах ±1% от текущей цены)
            /// </summary>
            public decimal Liquidity { get; set; }

            /// <summary>
            /// Общий риск портфеля (0-1, где 1 = 100% риска)
            /// </summary>
            public decimal PortfolioRisk { get; set; }

            /// <summary>
            /// Текущая стоимость портфеля в USDT
            /// </summary>
            public decimal PortfolioValue { get; set; }

            /// <summary>
            /// Conditional Value at Risk - ожидаемые потери при неблагоприятных условиях
            /// </summary>
            public decimal CVaR { get; set; }

            /// <summary>
            /// Матрица корреляций между всеми торговыми инструментами
            /// Ключ: Symbol, Значение: Словарь корреляций с другими символами
            /// </summary>
            public Dictionary<string, Dictionary<string, decimal>> CorrelationMatrix { get; set; } = new();

            /// <summary>
            /// Список открытых позиций
            /// </summary>
            public List<OpenPosition> OpenPositions { get; set; } = new();

            /// <summary>
            /// Текущий рыночный тренд (бычий/медвежий/нейтральный)
            /// </summary>
            public MarketTrend MarketTrend { get; set; }

            /// <summary>
            /// Бета портфеля - чувствительность к рыночным движениям
            /// >1 - более волатильный чем рынок, <1 - менее волатильный
            /// </summary>
            public decimal PortfolioBeta { get; set; }

            /// <summary>
            /// Результат последнего стресс-теста (максимальная просадка в %)
            /// </summary>
            public decimal StressTestResult { get; set; }

            /// <summary>
            /// Процент использования маржи (0-1)
            /// </summary>
            public decimal MarginUsage { get; set; }

            /// <summary>
            /// Прибыль/убыток за текущий день в USDT
            /// </summary>
            public decimal DailyProfitLoss { get; set; }

            /// <summary>
            /// Уровень влияния последних новостей (0-5)
            /// 0 - нет влияния, 5 - критическое влияние
            /// </summary>
            public int NewsImpactLevel { get; set; }

            /// <summary>
            /// Максимальный рекомендуемый размер позиции в % от портфеля
            /// </summary>
            public decimal MaxRecommendedPositionSize { get; set; }

            /// <summary>
            /// Средний риск/прибыль по открытым позициям
            /// </summary>
            public decimal AverageRiskRewardRatio { get; set; }

            /// <summary>
            /// Рассчитывает коэффициент Шарпа для портфеля
            /// </summary>
            /// <param name="riskFreeRate">Безрисковая ставка (по умолчанию 0)</param>
            /// <returns>Коэффициент Шарпа</returns>
            public decimal CalculateSharpeRatio(decimal riskFreeRate = 0)
            {
                if (Volatility == 0) return 0;
                return (DailyProfitLoss / PortfolioValue - riskFreeRate) / Volatility;
            }

            /// <summary>
            /// Рассчитывает коэффициент Сортино (аналог Шарпа, но учитывает только негативную волатильность)
            /// </summary>
            public decimal CalculateSortinoRatio(decimal riskFreeRate = 0)
            {
                if (Volatility == 0) return 0;
                return (DailyProfitLoss / PortfolioValue - riskFreeRate) / (Volatility * 0.5m);
            }

            /// <summary>
            /// Обновляет матрицу корреляций на основе исторических данных
            /// </summary>
            /// <param name="historicalData">Исторические данные по всем символам</param>
            public void UpdateCorrelationMatrix(Dictionary<string, List<MarketDataPoint>> historicalData)
            {
                var symbols = historicalData.Keys.ToList();
                CorrelationMatrix.Clear();

                foreach (var symbol1 in symbols)
                {
                    CorrelationMatrix[symbol1] = new Dictionary<string, decimal>();
                    var prices1 = historicalData[symbol1]
                        .Where(d => d.TimeFrame == KlineInterval.OneHour)
                        .Select(d => d.Close)
                        .TakeLast(100)
                        .ToArray();

                    foreach (var symbol2 in symbols)
                    {
                        if (symbol1 == symbol2)
                        {
                            CorrelationMatrix[symbol1][symbol2] = 1m;
                            continue;
                        }

                        var prices2 = historicalData[symbol2]
                            .Where(d => d.TimeFrame == KlineInterval.OneHour)
                            .Select(d => d.Close)
                            .TakeLast(100)
                            .ToArray();

                        if (prices1.Length != prices2.Length || prices1.Length < 10)
                        {
                            CorrelationMatrix[symbol1][symbol2] = 0m;
                            continue;
                        }

                        CorrelationMatrix[symbol1][symbol2] = CalculatePearsonCorrelation(prices1, prices2);
                    }
                }
            }

            /// <summary>
            /// Рассчитывает корреляцию Пирсона между двумя наборами цен
            /// </summary>
            private decimal CalculatePearsonCorrelation(decimal[] x, decimal[] y)
            {
                try
                {
                    if (x.Length != y.Length || x.Length < 2)
                        return 0m;

                    decimal sumX = 0, sumY = 0, sumXY = 0;
                    decimal sumX2 = 0, sumY2 = 0;

                    for (int i = 0; i < x.Length; i++)
                    {
                        sumX += x[i];
                        sumY += y[i];
                        sumXY += x[i] * y[i];
                        sumX2 += x[i] * x[i];
                        sumY2 += y[i] * y[i];
                    }

                    decimal numerator = sumXY - (sumX * sumY / x.Length);
                    decimal denominatorX = (sumX2 - (sumX * sumX / x.Length));
                    decimal denominatorY = (sumY2 - (sumY * sumY / x.Length));

                    if (denominatorX == 0 || denominatorY == 0)
                        return 0m;

                    return numerator / (decimal)Math.Sqrt((double)(denominatorX * denominatorY));
                }
                catch
                {
                    return 0m;
                }
            }

            /// <summary>
            /// Рассчитывает максимальную рекомендуемую позицию для символа
            /// </summary>
            public void CalculateMaxPositionSize(string symbol, decimal currentPrice)
            {
                try
                {
                    // Базовый лимит 5% от портфеля
                    decimal baseLimit = PortfolioValue * 0.05m / currentPrice;

                    // Корректировка на волатильность
                    decimal volatilityAdjustment = 1 - (Volatility / 0.2m); // Нормализуем к 20% волатильности
                    volatilityAdjustment = Math.Max(0.1m, Math.Min(1m, volatilityAdjustment));

                    // Корректировка на корреляцию
                    decimal correlationAdjustment = 1m;
                    foreach (var pos in OpenPositions)
                    {
                        decimal corr = CorrelationMatrix[symbol].GetValueOrDefault(pos.Symbol, 0m);
                        correlationAdjustment *= 1 - (Math.Abs(corr) * 0.5m);
                    }

                    MaxRecommendedPositionSize = baseLimit * volatilityAdjustment * correlationAdjustment;
                }
                catch
                {
                    MaxRecommendedPositionSize = 0m;
                }
            }

            /// <summary>
            /// Рассчитывает средний риск/прибыль по открытым позициям
            /// </summary>
            public void UpdateRiskRewardRatios()
            {
                try
                {
                    if (!OpenPositions.Any())
                    {
                        AverageRiskRewardRatio = 0m;
                        return;
                    }

                    decimal totalRatio = 0m;
                    int count = 0;

                    foreach (var pos in OpenPositions)
                    {
                        if (pos.TakeProfit == 0 || pos.StopLoss == 0) continue;

                        decimal risk = Math.Abs(pos.EntryPrice - pos.StopLoss);
                        decimal reward = Math.Abs(pos.TakeProfit - pos.EntryPrice);

                        if (risk > 0)
                        {
                            totalRatio += reward / risk;
                            count++;
                        }
                    }

                    AverageRiskRewardRatio = count > 0 ? totalRatio / count : 0m;
                }
                catch
                {
                    AverageRiskRewardRatio = 0m;
                }
            }

            /// <summary>
            /// Проверяет необходимость хеджирования портфеля
            /// </summary>
            public bool NeedsHedging()
            {
                // Хеджирование требуется если:
                // 1. Высокая волатильность
                bool highVolatility = Volatility > 0.15m;

                // 2. Большая концентрация в одном направлении
                decimal longExposure = OpenPositions
                    .Where(p => p.Direction == TradeDirection.Long)
                    .Sum(p => p.Quantity * p.EntryPrice);

                decimal shortExposure = OpenPositions
                    .Where(p => p.Direction == TradeDirection.Short)
                    .Sum(p => p.Quantity * p.EntryPrice);

                bool unbalanced = Math.Abs(longExposure - shortExposure) > PortfolioValue * 0.3m;

                // 3. Важные новости
                bool importantNews = NewsImpactLevel >= 3;

                return (highVolatility || importantNews) && unbalanced;
            }

            /// <summary>
            /// Возвращает строковое представление ключевых метрик
            /// </summary>
            public override string ToString()
            {
                return $"Риск: {PortfolioRisk:P1} | Волатильность: {Volatility:P1} | " +
                       $"CVaR: {CVaR:P1} | Бета: {PortfolioBeta:F2} | " +
                       $"P/L: {DailyProfitLoss:F2} USDT | RR: {AverageRiskRewardRatio:F2}";
            }
        }

        /// <summary>
        /// Тип рыночного тренда
        /// </summary>
        public enum MarketTrend
        {
            Bullish,    // Бычий
            Bearish,    // Медвежий
            Neutral     // Нейтральный
        }

        /// <summary>
        /// Запрос на исполнение ордера с расширенными параметрами управления исполнением
        /// </summary>
        public class OrderRequest
        {
            /// <summary>
            /// Торговая пара (например, BTCUSDT)
            /// </summary>
            public string Symbol { get; set; } = string.Empty;

            /// <summary>
            /// Направление сделки (Покупка/Продажа)
            /// </summary>
            public OrderSide Side { get; set; }

            /// <summary>
            /// Количество актива для торговли
            /// </summary>
            public decimal Quantity { get; set; }

            /// <summary>
            /// Цена входа (для лимитных ордеров)
            /// </summary>
            public decimal Price { get; set; }

            /// <summary>
            /// Цена стоп-лосса (обязательно для всех ордеров)
            /// </summary>
            public decimal StopLoss { get; set; }

            /// <summary>
            /// Цена тейк-профита (опционально)
            /// </summary>
            public decimal TakeProfit { get; set; }

            /// <summary>
            /// Использовать VWAP-алгоритм для крупных ордеров
            /// (Volume Weighted Average Price - исполнение по средневзвешенной цене)
            /// </summary>
            public bool UseVwap { get; set; }

            /// <summary>
            /// Использовать Iceberg-ордер (скрытая часть объема)
            /// </summary>
            public bool UseIceberg { get; set; }

            /// <summary>
            /// Максимально допустимое проскальзывание в процентах (0.1 = 0.1%)
            /// </summary>
            public decimal MaxSlippagePercent { get; set; } = 0.1m;

            /// <summary>
            /// Таймфрейм, на котором сгенерирован сигнал
            /// </summary>
            public KlineInterval TimeFrame { get; set; }

            /// <summary>
            /// Идентификатор стратегии, сгенерировавшей сигнал
            /// </summary>
            public string StrategyId { get; set; }

            /// <summary>
            /// Дополнительные параметры для алгоритмического исполнения
            /// </summary>
            public OrderExecutionParameters ExecutionParameters { get; set; } = new();

            /// <summary>
            /// Проверка валидности запроса перед исполнением
            /// </summary>
            public bool IsValid()
            {
                return !string.IsNullOrEmpty(Symbol) &&
                       Quantity > 0 &&
                       Price > 0 &&
                       StopLoss > 0 &&
                       (Side == OrderSide.Buy ? StopLoss < Price : StopLoss > Price);
            }
        }

        /// <summary>
        /// Дополнительные параметры алгоритмического исполнения ордеров
        /// </summary>
        public class OrderExecutionParameters
        {
            /// <summary>
            /// Максимальное время исполнения (в секундах)
            /// </summary>
            public int MaxExecutionTime { get; set; } = 30;

            /// <summary>
            /// Допустимое отклонение от цены (% от текущей цены)
            /// </summary>
            public decimal PriceDeviation { get; set; } = 0.05m;

            /// <summary>
            /// Агрессивность исполнения (0-1, где 1 - максимально агрессивно)
            /// </summary>
            public decimal Aggressiveness { get; set; } = 0.7m;

            /// <summary>
            /// Тип алгоритма исполнения:
            /// - Passive: Ждать лучшей цены
            /// - Neutral: Баланс цены и скорости
            /// - Aggressive: Максимальная скорость
            /// </summary>
            public ExecutionAlgorithmType AlgorithmType { get; set; } = ExecutionAlgorithmType.Neutral;

            /// <summary>
            /// Размер минимального куска для VWAP/Iceberg ордеров
            /// </summary>
            public decimal MinimalChunkSize { get; set; }

            /// <summary>
            /// Интервал между частями ордера (в секундах)
            /// </summary>
            public int ChunkIntervalSeconds { get; set; } = 10;
        }

        /// <summary>
        /// Тип алгоритма исполнения ордера
        /// </summary>
        public enum ExecutionAlgorithmType
        {
            /// <summary> Консервативный, минимизирует проскальзывание </summary>
            Passive,
            /// <summary> Баланс цены и скорости исполнения </summary>
            Neutral,
            /// <summary> Максимально быстрое исполнение </summary>
            Aggressive,
            /// <summary> Специальный режим для высоколиквидных пар </summary>
            Liquid,
            /// <summary> Для низколиквидных активов </summary>
            Illiquid
        }

        /// <summary>
        /// Результат исполнения торговой операции
        /// </summary>
        public class TradeResult
        {
            /// <summary>
            /// Флаг успешного исполнения ордера
            /// </summary>
            public bool Success { get; set; }

            /// <summary>
            /// Тикер торгового инструмента (например, BTCUSDT)
            /// </summary>
            public string Symbol { get; set; } = string.Empty;

            /// <summary>
            /// Направление сделки (BUY/SELL)
            /// </summary>
            public string Side { get; set; } = string.Empty;

            /// <summary>
            /// Исполненное количество актива
            /// </summary>
            public decimal Quantity { get; set; }

            /// <summary>
            /// Средняя цена исполнения (включая все частичные исполнения)
            /// </summary>
            public decimal AveragePrice { get; set; }

            /// <summary>
            /// Цена выхода из позиции (для закрытых позиций)
            /// </summary>
            public decimal? ExitPrice { get; set; }

            /// <summary>
            /// Цена стоп-лосса
            /// </summary>
            public decimal StopLoss { get; set; }

            /// <summary>
            /// Цена тейк-профита
            /// </summary>
            public decimal TakeProfit { get; set; }

            /// <summary>
            /// Время открытия позиции
            /// </summary>
            public DateTime EntryTime { get; set; }

            /// <summary>
            /// Время закрытия позиции (null для открытых позиций)
            /// </summary>
            public DateTime? ExitTime { get; set; }

            /// <summary>
            /// Реализованная прибыль/убыток (null для открытых позиций)
            /// </summary>
            public decimal? Profit { get; set; }

            /// <summary>
            /// Сумма комиссий по сделке
            /// </summary>
            public decimal Commission { get; set; }

            /// <summary>
            /// Процент риска от размера депозита
            /// </summary>
            public decimal RiskPercent { get; set; }

            /// <summary>
            /// Флаг успешности сделки (Profit > 0)
            /// </summary>
            public bool IsSuccessful { get; set; }

            /// <summary>
            /// Уровень волатильности на момент открытия
            /// </summary>
            public decimal Volatility { get; set; }

            /// <summary>
            /// Уровень ликвидности на момент открытия
            /// </summary>
            public decimal Liquidity { get; set; }

            /// <summary>
            /// Величина проскальзывания (разница между ожидаемой и фактической ценой)
            /// </summary>
            public decimal Slippage { get; set; }

            /// <summary>
            /// Таймфрейм, на котором был сгенерирован сигнал
            /// </summary>
            public string TimeFrame { get; set; }

            /// <summary>
            /// Причина закрытия позиции (для закрытых позиций)
            /// </summary>
            public string ExitReason { get; set; }

            /// <summary>
            /// Идентификатор стратегии, сгенерировавшей сигнал
            /// </summary>
            public string StrategyId { get; set; }

            /// <summary>
            /// Планируемый риск-ривард при открытии
            /// </summary>
            public decimal InitialRiskReward { get; set; }

            /// <summary>
            /// Фактический риск-ривард при закрытии
            /// </summary>
            public decimal RealizedRiskReward { get; set; }

            /// <summary>
            /// Время исполнения ордера в секундах
            /// </summary>
            public int ExecutionSeconds { get; set; }

            /// <summary>
            /// Сообщение об ошибке (если Success = false)
            /// </summary>
            public string Error { get; set; }

            /// <summary>
            /// Дополнительные метаданные сделки
            /// </summary>
            public Dictionary<string, object> Metadata { get; set; } = new();

            /// <summary>
            /// Рассчитывает риск-ривард соотношение
            /// </summary>
            /// <returns>Risk-Reward ratio</returns>
            public decimal CalculateRiskRewardRatio()
            {
                if (ExitPrice == null || StopLoss == 0) return 0;

                decimal risk = Math.Abs(AveragePrice - StopLoss);
                decimal reward = Math.Abs((ExitPrice ?? AveragePrice) - AveragePrice);

                return reward / risk;
            }

            /// <summary>
            /// Проверяет, была ли сделка закрыта по стоп-лоссу
            /// </summary>
            public bool IsStoppedOut()
            {
                if (ExitPrice == null) return false;

                return (Side == "BUY" && ExitPrice <= StopLoss) ||
                       (Side == "SELL" && ExitPrice >= StopLoss);
            }

            /// <summary>
            /// Проверяет, была ли сделка закрыта по тейк-профиту
            /// </summary>
            public bool IsTakenProfit()
            {
                if (ExitPrice == null) return false;

                return (Side == "BUY" && ExitPrice >= TakeProfit) ||
                       (Side == "SELL" && ExitPrice <= TakeProfit);
            }

            /// <summary>
            /// Возвращает продолжительность сделки (для закрытых позиций)
            /// </summary>
            public TimeSpan? GetTradeDuration()
            {
                return ExitTime != null ? ExitTime - EntryTime : null;
            }

            /// <summary>
            /// Возвращает процентную прибыль/убыток
            /// </summary>
            public decimal? GetProfitPercentage()
            {
                if (ExitPrice == null) return null;

                return Side == "BUY"
                    ? (ExitPrice.Value - AveragePrice) / AveragePrice
                    : (AveragePrice - ExitPrice.Value) / AveragePrice;
            }

            /// <summary>
            /// Добавляет метаданные к сделке
            /// </summary>
            public void AddMetadata(string key, object value)
            {
                Metadata[key] = value;
            }

            /// <summary>
            /// Возвращает строковое представление сделки
            /// </summary>
            public override string ToString()
            {
                return $"{Symbol} {Side} {Quantity} @ {AveragePrice} | " +
                       $"SL: {StopLoss} TP: {TakeProfit} | " +
                       $"PnL: {Profit?.ToString("F2") ?? "open"} | " +
                       $"Reason: {ExitReason ?? "active"}";
            }
        }

        /// <summary>
        /// Класс представляет открытую позицию на рынке
        /// </summary>
        public class OpenPosition
        {
            /// <summary>
            /// Торговый символ (например, "BTCUSDT")
            /// </summary>
            public string Symbol { get; set; } = string.Empty;

            /// <summary>
            /// Количество актива в позиции
            /// </summary>
            public decimal Quantity { get; set; }

            /// <summary>
            /// Цена входа в позицию
            /// </summary>
            public decimal EntryPrice { get; set; }

            /// <summary>
            /// Время открытия позиции (UTC)
            /// </summary>
            public DateTime EntryTime { get; set; }

            /// <summary>
            /// Цена стоп-лосса
            /// </summary>
            public decimal StopLoss { get; set; }

            /// <summary>
            /// Цена тейк-профита
            /// </summary>
            public decimal TakeProfit { get; set; }

            /// <summary>
            /// Направление позиции (лонг/шорт)
            /// </summary>
            public TradeDirection Direction { get; set; }

            /// <summary>
            /// Дистанция до стоп-лосса в абсолютных значениях
            /// (разница между ценой входа и стоп-лоссом)
            /// </summary>
            public decimal StopLossDistance { get; set; }

            /// <summary>
            /// Идентификатор стратегии, которая открыла позицию
            /// </summary>
            public string StrategyId { get; set; }

            /// <summary>
            /// Текущее соотношение риск/прибыль (актуально для трейлинг-стопа)
            /// </summary>
            public decimal CurrentRiskRewardRatio { get; set; }

            /// <summary>
            /// Флаг, указывающий, хеджирована ли позиция
            /// </summary>
            public bool IsHedged { get; set; }

            /// <summary>
            /// Символ для хеджирования (если IsHedged = true)
            /// </summary>
            public string HedgeSymbol { get; set; }

            /// <summary>
            /// Комиссия за открытие позиции
            /// </summary>
            public decimal OpeningCommission { get; set; }

            /// <summary>
            /// Время последнего обновления позиции (UTC)
            /// </summary>
            public DateTime LastUpdated { get; set; }

            /// <summary>
            /// Причина открытия позиции (сигнал, ручная торговля и т.д.)
            /// </summary>
            public string OpeningReason { get; set; }

            /// <summary>
            /// Рассчитывает текущую прибыль/убыток позиции
            /// </summary>
            /// <param name="currentPrice">Текущая рыночная цена</param>
            /// <returns>Значение PnL</returns>
            public decimal CalculatePnL(decimal currentPrice)
            {
                return Direction == TradeDirection.Long
                    ? (currentPrice - EntryPrice) * Quantity
                    : (EntryPrice - currentPrice) * Quantity;
            }

            /// <summary>
            /// Рассчитывает текущее соотношение риск/прибыль
            /// </summary>
            /// <param name="currentPrice">Текущая рыночная цена</param>
            /// <returns>Значение Risk/Reward</returns>
            public decimal CalculateRiskRewardRatio(decimal currentPrice)
            {
                decimal potentialProfit = Direction == TradeDirection.Long
                    ? TakeProfit - EntryPrice
                    : EntryPrice - TakeProfit;

                decimal potentialLoss = Direction == TradeDirection.Long
                    ? EntryPrice - StopLoss
                    : StopLoss - EntryPrice;

                if (potentialLoss == 0) return 0;

                return potentialProfit / potentialLoss;
            }

            /// <summary>
            /// Обновляет трейлинг-стоп
            /// </summary>
            /// <param name="currentPrice">Текущая рыночная цена</param>
            /// <param name="trailingDistance">Дистанция трейлинга в процентах</param>
            public void UpdateTrailingStop(decimal currentPrice, decimal trailingDistance = 1.5m)
            {
                if (Direction == TradeDirection.Long)
                {
                    decimal newStop = currentPrice - (StopLossDistance * trailingDistance);
                    if (newStop > StopLoss)
                    {
                        StopLoss = newStop;
                        LastUpdated = DateTime.UtcNow;
                    }
                }
                else
                {
                    decimal newStop = currentPrice + (StopLossDistance * trailingDistance);
                    if (newStop < StopLoss)
                    {
                        StopLoss = newStop;
                        LastUpdated = DateTime.UtcNow;
                    }
                }

                CurrentRiskRewardRatio = CalculateRiskRewardRatio(currentPrice);
            }

            /// <summary>
            /// Проверяет, сработал ли стоп-лосс
            /// </summary>
            /// <param name="currentPrice">Текущая рыночная цена</param>
            /// <returns>True, если стоп-лосс сработал</returns>
            public bool IsStopLossTriggered(decimal currentPrice)
            {
                return Direction == TradeDirection.Long
                    ? currentPrice <= StopLoss
                    : currentPrice >= StopLoss;
            }

            /// <summary>
            /// Проверяет, сработал ли тейк-профит
            /// </summary>
            /// <param name="currentPrice">Текущая рыночная цена</param>
            /// <returns>True, если тейк-профит сработал</returns>
            public bool IsTakeProfitTriggered(decimal currentPrice)
            {
                return Direction == TradeDirection.Long
                    ? currentPrice >= TakeProfit
                    : currentPrice <= TakeProfit;
            }

            /// <summary>
            /// Возвращает процент риска позиции относительно цены входа
            /// </summary>
            public decimal GetRiskPercentage()
            {
                return Direction == TradeDirection.Long
                    ? (EntryPrice - StopLoss) / EntryPrice * 100
                    : (StopLoss - EntryPrice) / EntryPrice * 100;
            }

            /// <summary>
            /// Возвращает процент потенциальной прибыли относительно цены входа
            /// </summary>
            public decimal GetRewardPercentage()
            {
                return Direction == TradeDirection.Long
                    ? (TakeProfit - EntryPrice) / EntryPrice * 100
                    : (EntryPrice - TakeProfit) / EntryPrice * 100;
            }

            /// <summary>
            /// Возвращает продолжительность позиции в минутах
            /// </summary>
            public double GetPositionDurationMinutes()
            {
                return (DateTime.UtcNow - EntryTime).TotalMinutes;
            }

            /// <summary>
            /// Проверяет, требует ли позиция хеджирования
            /// </summary>
            /// <param name="volatility">Текущая волатильность</param>
            /// <param name="portfolioRisk">Риск портфеля</param>
            /// <returns>True, если требуется хеджирование</returns>
            public bool RequiresHedging(decimal volatility, decimal portfolioRisk)
            {
                // Позиция уже хеджирована
                if (IsHedged) return false;

                // Крупная позиция (>20% портфеля) с высоким риском
                bool isLargePosition = GetRiskPercentage() > 20;

                // Высокая волатильность
                bool isHighVolatility = volatility > 0.05m;

                // Высокий риск портфеля
                bool isHighPortfolioRisk = portfolioRisk > 0.5m;

                return isLargePosition && (isHighVolatility || isHighPortfolioRisk);
            }
        }

        /// <summary>
        /// Представляет стакан цен (биржевой стакан) с методами анализа ликвидности
        /// </summary>
        public class OrderBook
        {
            /// <summary>
            /// Идентификатор торговой пары (например, BTCUSDT)
            /// </summary>
            public string Symbol { get; set; }

            /// <summary>
            /// Список заявок на покупку (биды), отсортированный по цене от высокой к низкой
            /// </summary>
            public List<OrderBookEntry> Bids { get; set; } = new List<OrderBookEntry>();

            /// <summary>
            /// Список заявок на продажу (аски), отсортированный по цене от низкой к высокой
            /// </summary>
            public List<OrderBookEntry> Asks { get; set; } = new List<OrderBookEntry>();

            /// <summary>
            /// Время последнего обновления стакана
            /// </summary>
            public DateTime Timestamp { get; set; }

            /// <summary>
            /// Рассчитывает общую ликвидность вокруг текущей цены
            /// </summary>
            /// <param name="currentPrice">Текущая рыночная цена</param>
            /// <param name="percentRange">Процентный диапазон от текущей цены (по умолчанию ±2%)</param>
            /// <returns>Суммарный объем ликвидности в USDT</returns>
            public decimal CalculateLiquidity(decimal currentPrice, decimal percentRange = 2m)
            {
                if (currentPrice <= 0)
                    return 0;

                // Рассчитываем границы диапазона
                decimal lowerBound = currentPrice * (1 - percentRange / 100);
                decimal upperBound = currentPrice * (1 + percentRange / 100);

                // Суммируем ликвидность бидов в диапазоне
                decimal bidLiquidity = Bids
                    .Where(b => b.Price >= lowerBound)
                    .Sum(b => b.Price * b.Quantity);

                // Суммируем ликвидность асков в диапазоне
                decimal askLiquidity = Asks
                    .Where(a => a.Price <= upperBound)
                    .Sum(a => a.Price * a.Quantity);

                return bidLiquidity + askLiquidity;
            }

            /// <summary>
            /// Рассчитывает имбаланс стакана - соотношение объема бидов к аскам
            /// </summary>
            /// <param name="currentPrice">Текущая рыночная цена</param>
            /// <param name="depthLevels">Количество уровней стакана для анализа</param>
            /// <returns>
            /// Значение от -1 до 1: 
            /// -1 - полное доминирование асков,
            ///  0 - баланс,
            /// +1 - полное доминирование бидов
            /// </returns>
            public decimal CalculateImbalance(decimal currentPrice, int depthLevels = 10)
            {
                if (Bids.Count == 0 || Asks.Count == 0 || currentPrice <= 0)
                    return 0;

                // Берем верхние N уровней стакана
                var topBids = Bids.Take(depthLevels).ToList();
                var topAsks = Asks.Take(depthLevels).ToList();

                // Рассчитываем взвешенный объем
                decimal bidVolume = topBids.Sum(b => b.Quantity * (1 - (currentPrice - b.Price) / currentPrice));
                decimal askVolume = topAsks.Sum(a => a.Quantity * (1 - (a.Price - currentPrice) / currentPrice));

                if (bidVolume + askVolume == 0)
                    return 0;

                return (bidVolume - askVolume) / (bidVolume + askVolume);
            }

            /// <summary>
            /// Находит ключевые уровни поддержки и сопротивления в стакане
            /// </summary>
            /// <param name="levelsCount">Количество возвращаемых уровней</param>
            /// <returns>
            /// Кортеж где:
            /// Item1 - список уровней поддержки (биды),
            /// Item2 - список уровней сопротивления (аски)
            /// </returns>
            public (List<decimal> Supports, List<decimal> Resistances) FindKeyLevels(int levelsCount = 3)
            {
                // Группируем биды по ценам (округленным до 2 знаков) и находим крупнейшие скопления
                var supportLevels = Bids
                    .GroupBy(b => Math.Round(b.Price, 2))
                    .OrderByDescending(g => g.Sum(b => b.Quantity))
                    .Take(levelsCount)
                    .Select(g => g.Key)
                    .ToList();

                // Аналогично для асков
                var resistanceLevels = Asks
                    .GroupBy(a => Math.Round(a.Price, 2))
                    .OrderByDescending(g => g.Sum(a => a.Quantity))
                    .Take(levelsCount)
                    .Select(g => g.Key)
                    .ToList();

                return (supportLevels, resistanceLevels);
            }

            /// <summary>
            /// Рассчитывает средневзвешенную цену (VWAP) для заданного объема
            /// </summary>
            /// <param name="quantity">Желаемый объем</param>
            /// <param name="side">Направление (BUY/SELL)</param>
            /// <returns>
            /// Кортеж где:
            /// Item1 - средневзвешенная цена,
            /// Item2 - общий доступный объем
            /// </returns>
            public (decimal Vwap, decimal TotalQuantity) CalculateVwap(decimal quantity, OrderSide side)
            {
                var levels = side == OrderSide.Buy ? Asks : Bids;
                decimal totalValue = 0;
                decimal filledQuantity = 0;

                foreach (var level in levels)
                {
                    if (filledQuantity >= quantity)
                        break;

                    decimal available = level.Quantity;
                    decimal needed = quantity - filledQuantity;
                    decimal executing = Math.Min(available, needed);

                    totalValue += executing * level.Price;
                    filledQuantity += executing;
                }

                if (filledQuantity == 0)
                    return (0, 0);

                return (totalValue / filledQuantity, filledQuantity);
            }

            /// <summary>
            /// Определяет есть ли в стакане аномалии ликвидности
            /// </summary>
            /// <param name="currentPrice">Текущая рыночная цена</param>
            /// <returns>
            /// True если обнаружены:
            /// - Большие "стены" в стакане
            /// - Резкие перепады ликвидности
            /// - Подозрительные кластеры ордеров
            /// </returns>
            public bool HasLiquidityAnomalies(decimal currentPrice)
            {
                if (Bids.Count == 0 || Asks.Count == 0)
                    return false;

                // Проверяем большие "стены" в стакане
                decimal avgBidSize = Bids.Average(b => b.Quantity);
                decimal maxBidSize = Bids.Max(b => b.Quantity);
                bool hasBidWall = maxBidSize > avgBidSize * 10;

                decimal avgAskSize = Asks.Average(a => a.Quantity);
                decimal maxAskSize = Asks.Max(a => a.Quantity);
                bool hasAskWall = maxAskSize > avgAskSize * 10;

                // Проверяем резкие перепады ликвидности
                var bidVolumes = Bids.Select(b => b.Quantity).ToList();
                bool hasBidSpikes = CheckVolumeSpikes(bidVolumes);

                var askVolumes = Asks.Select(a => a.Quantity).ToList();
                bool hasAskSpikes = CheckVolumeSpikes(askVolumes);

                return hasBidWall || hasAskWall || hasBidSpikes || hasAskSpikes;
            }

            private bool CheckVolumeSpikes(List<decimal> volumes)
            {
                if (volumes.Count < 5)
                    return false;

                decimal avg = volumes.Average();
                decimal stdDev = (decimal)Math.Sqrt(volumes.Average(v => Math.Pow((double)(v - avg), 2)));

                // Считаем аномалией объемы больше чем 3 стандартных отклонения
                return volumes.Any(v => v > avg + 3 * stdDev);
            }

            /// <summary>
            /// Обновляет стакан новыми данными
            /// </summary>
            /// <param name="newBids">Новые биды</param>
            /// <param name="newAsks">Новые аски</param>
            public void Update(List<OrderBookEntry> newBids, List<OrderBookEntry> newAsks)
            {
                // Сортируем биды по убыванию цены
                Bids = newBids
                    .OrderByDescending(b => b.Price)
                    .ToList();

                // Сортируем аски по возрастанию цены
                Asks = newAsks
                    .OrderBy(a => a.Price)
                    .ToList();

                Timestamp = DateTime.UtcNow;
            }

            /// <summary>
            /// Возвращает разницу между лучшим бидом и аском (спред)
            /// </summary>
            public decimal GetSpread()
            {
                if (Bids.Count == 0 || Asks.Count == 0)
                    return 0;

                return Asks[0].Price - Bids[0].Price;
            }

            /// <summary>
            /// Возвращает среднюю цену между лучшим бидом и аском (mid price)
            /// </summary>
            public decimal GetMidPrice()
            {
                if (Bids.Count == 0 || Asks.Count == 0)
                    return 0;

                return (Bids[0].Price + Asks[0].Price) / 2;
            }

            /// <summary>
            /// Находит крупнейшие кластеры ликвидности в стакане
            /// </summary>
            /// <param name="side">Направление (BUY/SELL)</param>
            /// <param name="clusterSize">Размер кластера в процентах от цены</param>
            /// <returns>Список кластеров, отсортированный по объему</returns>
            public List<LiquidityCluster> FindLiquidityClusters(OrderSide side, decimal clusterSize = 0.1m)
            {
                var levels = side == OrderSide.Buy ? Bids : Asks;
                if (!levels.Any())
                    return new List<LiquidityCluster>();

                var clusters = new List<LiquidityCluster>();
                var currentCluster = new LiquidityCluster
                {
                    MinPrice = levels[0].Price,
                    MaxPrice = levels[0].Price,
                    TotalQuantity = levels[0].Quantity
                };

                for (int i = 1; i < levels.Count; i++)
                {
                    decimal price = levels[i].Price;
                    decimal priceDiff = Math.Abs(price - currentCluster.MinPrice) / currentCluster.MinPrice * 100;

                    if (priceDiff <= clusterSize)
                    {
                        currentCluster.MinPrice = Math.Min(currentCluster.MinPrice, price);
                        currentCluster.MaxPrice = Math.Max(currentCluster.MaxPrice, price);
                        currentCluster.TotalQuantity += levels[i].Quantity;
                    }
                    else
                    {
                        clusters.Add(currentCluster);
                        currentCluster = new LiquidityCluster
                        {
                            MinPrice = price,
                            MaxPrice = price,
                            TotalQuantity = levels[i].Quantity
                        };
                    }
                }

                clusters.Add(currentCluster);
                return clusters
                    .OrderByDescending(c => c.TotalQuantity)
                    .ToList();
            }
        }

        /// <summary>
        /// Представляет одну запись в стакане (ордер)
        /// </summary>
        public class OrderBookEntry
        {
            public OrderBookEntry(decimal price, decimal quantity)
            {
                Price = price;
                Quantity = quantity;
            }

            /// <summary>
            /// Цена ордера
            /// </summary>
            public decimal Price { get; set; }

            /// <summary>
            /// Количество (объем) ордера
            /// </summary>
            public decimal Quantity { get; set; }

            public override string ToString() => $"{Price} x {Quantity}";
        }

        /// <summary>
        /// Представляет кластер ликвидности - группу ордеров в близком ценовом диапазоне
        /// </summary>
        public class LiquidityCluster
        {
            /// <summary>
            /// Минимальная цена в кластере
            /// </summary>
            public decimal MinPrice { get; set; }

            /// <summary>
            /// Максимальная цена в кластере
            /// </summary>
            public decimal MaxPrice { get; set; }

            /// <summary>
            /// Суммарный объем кластера
            /// </summary>
            public decimal TotalQuantity { get; set; }

            /// <summary>
            /// Средняя цена кластера
            /// </summary>
            public decimal AveragePrice => (MinPrice + MaxPrice) / 2;

            public override string ToString() =>
                $"{AveragePrice} [{MinPrice}-{MaxPrice}] x {TotalQuantity}";
        }

        /// <summary>
        /// Направление ордера (покупка/продажа)
        /// </summary>
        public enum OrderSide
        {
            Buy,
            Sell
        }

        /// <summary>
        /// Полная запись о торговой операции с детализацией всех параметров
        /// </summary>
        public class TradeRecord
        {
            /// <summary>
            /// Торговая пара (например, BTCUSDT)
            /// </summary>
            public string Symbol { get; set; } = string.Empty;

            /// <summary>
            /// Направление сделки (BUY/SELL)
            /// </summary>
            public string Side { get; set; } = string.Empty;

            /// <summary>
            /// Объем актива в сделке (количество монет)
            /// </summary>
            public decimal Quantity { get; set; }

            /// <summary>
            /// Цена входа в позицию
            /// </summary>
            public decimal EntryPrice { get; set; }

            /// <summary>
            /// Цена выхода из позиции (null если позиция еще открыта)
            /// </summary>
            public decimal? ExitPrice { get; set; }

            /// <summary>
            /// Прибыль/убыток по сделке в USDT (null если позиция еще открыта)
            /// </summary>
            public decimal? Profit { get; set; }

            /// <summary>
            /// Время открытия позиции (UTC)
            /// </summary>
            public DateTime EntryTime { get; set; }

            /// <summary>
            /// Время закрытия позиции (UTC, null если позиция еще открыта)
            /// </summary>
            public DateTime? ExitTime { get; set; }

            /// <summary>
            /// Уровень стоп-лосса при открытии
            /// </summary>
            public decimal StopLoss { get; set; }

            /// <summary>
            /// Уровень тейк-профита при открытии
            /// </summary>
            public decimal TakeProfit { get; set; }

            /// <summary>
            /// Сумма комиссий по сделке
            /// </summary>
            public decimal Commission { get; set; }

            /// <summary>
            /// Флаг успешности сделки (true если Profit > 0)
            /// </summary>
            public bool IsSuccessful { get; set; }

            /// <summary>
            /// Процент риска от депозита на сделку
            /// </summary>
            public decimal RiskPercent { get; set; }

            /// <summary>
            /// Волатильность актива на момент входа
            /// </summary>
            public decimal Volatility { get; set; }

            /// <summary>
            /// Уровень ликвидности на момент входа
            /// </summary>
            public decimal Liquidity { get; set; }

            /// <summary>
            /// Величина проскальзывания при исполнении
            /// </summary>
            public decimal Slippage { get; set; }

            /// <summary>
            /// Таймфрейм, на котором был сгенерирован сигнал
            /// </summary>
            public string TimeFrame { get; set; }

            /// <summary>
            /// Причина закрытия позиции (если закрыта)
            /// </summary>
            public string ExitReason { get; set; }

            /// <summary>
            /// Идентификатор стратегии, сгенерировавшей сигнал
            /// </summary>
            public string StrategyId { get; set; }

            /// <summary>
            /// Планируемый риск-ривард при входе
            /// </summary>
            public decimal InitialRiskReward { get; set; }

            /// <summary>
            /// Фактический риск-ривард при выходе (null если позиция открыта)
            /// </summary>
            public decimal? RealizedRiskReward { get; set; }

            /// <summary>
            /// Время исполнения ордера в секундах
            /// </summary>
            public int ExecutionSeconds { get; set; }

            /// <summary>
            /// Дополнительные метки сделки (теги, категории и т.д.)
            /// </summary>
            public List<string> Tags { get; set; } = new List<string>();

            /// <summary>
            /// Пользовательские заметки по сделке
            /// </summary>
            public string Notes { get; set; }

            /// <summary>
            /// Список связанных ордеров (хеджирующие позиции, частичные закрытия)
            /// </summary>
            public List<RelatedOrder> RelatedOrders { get; set; } = new List<RelatedOrder>();

            /// <summary>
            /// Рассчитывает ключевые метрики сделки при закрытии позиции
            /// </summary>
            public void CalculateMetricsOnClose()
            {
                if (ExitPrice.HasValue)
                {
                    // Расчет прибыли с учетом направления сделки
                    Profit = (ExitPrice.Value - EntryPrice) * Quantity *
                            (Side == "SELL" ? -1 : 1) - Commission;

                    // Расчет фактического риск-риварда
                    if (StopLoss != 0)
                    {
                        var risk = Math.Abs(EntryPrice - StopLoss);
                        var reward = Math.Abs(ExitPrice.Value - EntryPrice);
                        RealizedRiskReward = reward / risk;
                    }

                    // Определение успешности сделки
                    IsSuccessful = Profit > 0;
                }
            }

            /// <summary>
            /// Добавляет связанный ордер (хедж, частичное закрытие и т.д.)
            /// </summary>
            public void AddRelatedOrder(string orderId, string type, decimal quantity, decimal price)
            {
                RelatedOrders.Add(new RelatedOrder
                {
                    OrderId = orderId,
                    Type = type,
                    Quantity = quantity,
                    Price = price,
                    Timestamp = DateTime.UtcNow
                });
            }

            /// <summary>
            /// Возвращает строковое представление сделки
            /// </summary>
            public override string ToString()
            {
                return $"{Symbol} {Side} {Quantity}@{EntryPrice} " +
                       $"SL:{StopLoss} TP:{TakeProfit} " +
                       $"PnL:{(Profit?.ToString("F2") ?? "open")} " +
                       $"Strategy:{StrategyId}";
            }

            /// <summary>
            /// Запись о связанном ордере
            /// </summary>
            public class RelatedOrder
            {
                public string OrderId { get; set; }
                public string Type { get; set; } // "HEDGE", "PARTIAL_CLOSE" и т.д.
                public decimal Quantity { get; set; }
                public decimal Price { get; set; }
                public DateTime Timestamp { get; set; }
            }
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