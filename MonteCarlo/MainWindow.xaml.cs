using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MonteCarlo;

public partial class MainWindow : Window
{
    // ── Globalna zmienna wynikowa (suma π z poszczególnych wątków) ──────────
    private double _globalPiSum = 0.0;
    private int    _globalThreadsFinished = 0;

    // Obiekt synchronizacji dostępu do zmiennych globalnych
    private readonly object _syncLock = new();

    // Liczba rdzeni procesora – tyle wątków uruchamiamy
    private readonly int _threadCount = Environment.ProcessorCount;

    // Ograniczenie liczby rysowanych punktów (wydajność UI)
    private const int MaxVisualPoints = 5000;

    // ── Zapamiętane wyniki obu metod (do porównania) ───────────────────────
    private double _lastPiThreads     = double.NaN;
    private double _lastPiThreadPool  = double.NaN;
    private double _lastTimeThreads   = double.NaN;
    private double _lastTimeThreadPool = double.NaN;

    // ── Rozmiar obszaru rysowania ──────────────────────────────────────────
    private double _canvasSize = 400;

    public MainWindow()
    {
        InitializeComponent();
        LblCoreCount.Text = $"Rdzenie CPU: {_threadCount}";
        LblThreadCount.Text = $"Liczba wątków: {_threadCount}";
        DrawCircleGuide();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  METODA MONTE CARLO (wykonywana w każdym wątku)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Szacuje π metodą Monte Carlo dla <paramref name="iterations"/> losowań.
    /// Zwraca lokalny wynik (4 * trafia / iteracje) oraz
    /// listę punktów do wizualizacji (maks. <see cref="MaxVisualPoints"/>).
    /// </summary>
    private static (double pi, List<(double x, double y, bool inside)> points)
        MonteCarloWorker(int iterations, int maxPoints)
    {
        long inside = 0;
        var points = new List<(double, double, bool)>(Math.Min(iterations, maxPoints));
        // Collect every Nth point so total visual points ≈ maxPoints
        int step = iterations <= maxPoints ? 1 : iterations / maxPoints;

        for (int i = 0; i < iterations; i++)
        {
            double x = Random.Shared.NextDouble();
            double y = Random.Shared.NextDouble();
            bool hit = (x * x + y * y) <= 1.0;
            if (hit) inside++;

            if (i % step == 0)
                points.Add((x, y, hit));
        }

        double pi = 4.0 * inside / (double)iterations;
        return (pi, points);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  a) WĄTKI Z LISTY OBIEKTÓW Thread
    // ══════════════════════════════════════════════════════════════════════

    private void BtnRunThreads_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetIterations(out int totalIterations)) return;

        SetButtonsEnabled(false);
        ClearCanvas();
        DrawCircleGuide();

        LblThreadsStatus.Text = "Obliczanie…";
        LblPiThreads.Text     = "…";
        LblTimeThreads.Text   = "…";
        ProgressBar.Value     = 0;
        LblProgress.Text      = "";

        // Resetujemy globalną zmienną wynikową
        lock (_syncLock)
        {
            _globalPiSum          = 0.0;
            _globalThreadsFinished = 0;
        }

        int iterPerThread  = totalIterations / _threadCount;
        int maxPtsPerThread = MaxVisualPoints / _threadCount;

        // Lokalne zmienne zbierające punkty z każdego wątku
        var allPoints = new List<(double x, double y, bool inside)>[_threadCount];
        for (int i = 0; i < _threadCount; i++) allPoints[i] = new();

        var stopwatch  = Stopwatch.StartNew();
        var threads    = new List<Thread>(_threadCount);
        // CountdownEvent – synchronizacja głównego wątku z wątkami roboczymi
        var countdown = new CountdownEvent(_threadCount);

        for (int t = 0; t < _threadCount; t++)
        {
            int threadIndex = t;
            int iterations  = (t == _threadCount - 1)
                ? totalIterations - iterPerThread * (_threadCount - 1)
                : iterPerThread;

            var thread = new Thread(() =>
            {
                var (localPi, points) = MonteCarloWorker(iterations, maxPtsPerThread);

                // Sekcja krytyczna – aktualizacja globalnej zmiennej wynikowej
                lock (_syncLock)
                {
                    _globalPiSum += localPi;
                    _globalThreadsFinished++;
                    allPoints[threadIndex] = points;
                }

                // Sygnalizujemy zakończenie wątku
                countdown.Signal();
            });

            thread.IsBackground = true;
            threads.Add(thread);
        }

        // Uruchamiamy wszystkie wątki
        foreach (var t in threads) t.Start();

        // Czekamy na zakończenie wszystkich wątków w osobnym wątku tła,
        // aby nie blokować wątku UI
        Thread waitThread = new(() =>
        {
            try
            {
                countdown.Wait();   // blokuje aż wszystkie wątki skończą
            }
            finally
            {
                countdown.Dispose();
            }
            stopwatch.Stop();

            // Obliczamy wynik jako średnią z wątków (zmienna globalna)
            double resultPi;
            lock (_syncLock)
            {
                resultPi = _globalPiSum / _globalThreadsFinished;
            }

            double elapsed = stopwatch.Elapsed.TotalMilliseconds;

            // Aktualizujemy UI w wątku dispatchera
            Dispatcher.Invoke(() =>
            {
                _lastPiThreads   = resultPi;
                _lastTimeThreads = elapsed;

                LblPiThreads.Text     = resultPi.ToString("F8");
                LblTimeThreads.Text   = $"{elapsed:F1} ms";
                LblThreadsStatus.Text = "Zakończono";

                // Rysujemy punkty
                var merged = allPoints.SelectMany(p => p).ToList();
                DrawPoints(merged);
                UpdateProgress(100, "Thread – zakończono");
                UpdateComparison();
                SetButtonsEnabled(true);
            });
        })
        { IsBackground = true };

        waitThread.Start();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  b) ZADANIA Z PULI WĄTKÓW ThreadPool
    // ══════════════════════════════════════════════════════════════════════

    private void BtnRunThreadPool_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetIterations(out int totalIterations)) return;

        SetButtonsEnabled(false);
        ClearCanvas();
        DrawCircleGuide();

        LblThreadPoolStatus.Text = "Obliczanie…";
        LblPiThreadPool.Text     = "…";
        LblTimeThreadPool.Text   = "…";
        ProgressBar.Value        = 0;
        LblProgress.Text         = "";

        // Resetujemy globalną zmienną wynikową
        lock (_syncLock)
        {
            _globalPiSum          = 0.0;
            _globalThreadsFinished = 0;
        }

        int iterPerThread   = totalIterations / _threadCount;
        int maxPtsPerThread = MaxVisualPoints / _threadCount;

        var allPoints = new List<(double x, double y, bool inside)>[_threadCount];
        for (int i = 0; i < _threadCount; i++) allPoints[i] = new();

        var stopwatch  = Stopwatch.StartNew();
        // CountdownEvent – synchronizacja głównego wątku z wątkami ThreadPool
        var countdown  = new CountdownEvent(_threadCount);

        for (int t = 0; t < _threadCount; t++)
        {
            int threadIndex = t;
            int iterations  = (t == _threadCount - 1)
                ? totalIterations - iterPerThread * (_threadCount - 1)
                : iterPerThread;

            // Kolejkujemy zadanie do puli wątków
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var (localPi, points) = MonteCarloWorker(iterations, maxPtsPerThread);

                // Sekcja krytyczna – aktualizacja globalnej zmiennej wynikowej
                lock (_syncLock)
                {
                    _globalPiSum += localPi;
                    _globalThreadsFinished++;
                    allPoints[threadIndex] = points;
                }

                // Sygnalizujemy zakończenie zadania
                countdown.Signal();
            });
        }

        // Czekamy na zakończenie wszystkich zadań w osobnym wątku tła
        Thread waitThread = new(() =>
        {
            try
            {
                countdown.Wait();   // blokuje aż wszystkie zadania ThreadPool skończą
            }
            finally
            {
                countdown.Dispose();
            }
            stopwatch.Stop();

            // Obliczamy wynik jako średnią (zmienna globalna)
            double resultPi;
            lock (_syncLock)
            {
                resultPi = _globalPiSum / _globalThreadsFinished;
            }

            double elapsed = stopwatch.Elapsed.TotalMilliseconds;

            // Aktualizujemy UI w wątku dispatchera
            Dispatcher.Invoke(() =>
            {
                _lastPiThreadPool   = resultPi;
                _lastTimeThreadPool = elapsed;

                LblPiThreadPool.Text     = resultPi.ToString("F8");
                LblTimeThreadPool.Text   = $"{elapsed:F1} ms";
                LblThreadPoolStatus.Text = "Zakończono";

                var merged = allPoints.SelectMany(p => p).ToList();
                DrawPoints(merged);
                UpdateProgress(100, "ThreadPool – zakończono");
                UpdateComparison();
                SetButtonsEnabled(true);
            });
        })
        { IsBackground = true };

        waitThread.Start();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  RYSOWANIE
    // ══════════════════════════════════════════════════════════════════════

    private void DrawCircleGuide()
    {
        double size = _canvasSize;

        // Tło kwadratu
        var rect = new Rectangle
        {
            Width  = size,
            Height = size,
            Fill   = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
            Stroke = Brushes.Gray,
            StrokeThickness = 1
        };
        Canvas.SetLeft(rect, 0);
        Canvas.SetTop(rect, 0);
        MonteCarloCanvas.Children.Add(rect);

        // Okrąg ćwiartki (x∈[0,1], y∈[0,1])
        var ellipse = new Ellipse
        {
            Width  = size * 2,
            Height = size * 2,
            Stroke = new SolidColorBrush(Color.FromRgb(50, 100, 200)),
            StrokeThickness = 2,
            Fill   = Brushes.Transparent
        };
        Canvas.SetLeft(ellipse, 0);
        Canvas.SetTop(ellipse, 0);
        MonteCarloCanvas.Children.Add(ellipse);
    }

    private void DrawPoints(List<(double x, double y, bool inside)> points)
    {
        double size = _canvasSize;
        foreach (var (x, y, inside) in points)
        {
            // Y-oś kanwy jest odwrócona względem układu matematycznego
            double cx = x * size;
            double cy = (1.0 - y) * size;

            var dot = new Ellipse
            {
                Width  = 3,
                Height = 3,
                Fill   = inside
                    ? new SolidColorBrush(Color.FromArgb(180, 30, 100, 200))
                    : new SolidColorBrush(Color.FromArgb(180, 220, 60, 50))
            };
            Canvas.SetLeft(dot, cx - 1.5);
            Canvas.SetTop(dot,  cy - 1.5);
            MonteCarloCanvas.Children.Add(dot);
        }
    }

    private void ClearCanvas()
    {
        MonteCarloCanvas.Children.Clear();
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _canvasSize = Math.Min(MonteCarloCanvas.ActualWidth, MonteCarloCanvas.ActualHeight);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  POMOCNICZE
    // ══════════════════════════════════════════════════════════════════════

    private void UpdateComparison()
    {
        if (double.IsNaN(_lastPiThreads) || double.IsNaN(_lastPiThreadPool))
        {
            LblComparison.Text = "Uruchom oba warianty, aby zobaczyć porównanie.";
            return;
        }

        double diff = _lastTimeThreadPool - _lastTimeThreads;
        string faster;
        if (Math.Abs(diff) < 5)
            faster = "Czasy są porównywalne.";
        else if (diff < 0)
            faster = $"ThreadPool szybszy o {-diff:F1} ms.";
        else
            faster = $"Thread szybszy o {diff:F1} ms.";

        LblComparison.Text =
            $"Thread:     π={_lastPiThreads:F6}, {_lastTimeThreads:F1} ms\n" +
            $"ThreadPool: π={_lastPiThreadPool:F6}, {_lastTimeThreadPool:F1} ms\n" +
            faster;
    }

    private void UpdateProgress(double value, string text)
    {
        ProgressBar.Value = value;
        LblProgress.Text  = text;
    }

    private void SetButtonsEnabled(bool enabled)
    {
        BtnRunThreads.IsEnabled    = enabled;
        BtnRunThreadPool.IsEnabled = enabled;
    }

    private bool TryGetIterations(out int iterations)
    {
        if (int.TryParse(TxtIterations.Text, out iterations) && iterations > 0)
            return true;

        MessageBox.Show("Podaj poprawną liczbę iteracji (liczba całkowita > 0).",
                        "Błędna wartość", MessageBoxButton.OK, MessageBoxImage.Warning);
        iterations = 0;
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ZDARZENIA MENU
    // ══════════════════════════════════════════════════════════════════════

    private void MenuIterations_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string tag)
            TxtIterations.Text = tag;
    }

    private void MenuClear_Click(object sender, RoutedEventArgs e)
    {
        ClearCanvas();
        DrawCircleGuide();
        LblPiThreads.Text     = "—";
        LblTimeThreads.Text   = "—";
        LblThreadsStatus.Text = "Gotowy";
        LblPiThreadPool.Text     = "—";
        LblTimeThreadPool.Text   = "—";
        LblThreadPoolStatus.Text = "Gotowy";
        LblComparison.Text = "Uruchom oba warianty, aby zobaczyć porównanie.";
        _lastPiThreads = _lastPiThreadPool = double.NaN;
        _lastTimeThreads = _lastTimeThreadPool = double.NaN;
        ProgressBar.Value = 0;
        LblProgress.Text = "";
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            $"Monte Carlo – szacowanie liczby π\n\n" +
            $"Metoda: losowe punkty w kwadracie [0,1]²\n" +
            $"π ≈ 4 × (punkty w ćwiartce koła) / (wszystkie punkty)\n\n" +
            $"Liczba wątków: {_threadCount} (rdzenie CPU)\n\n" +
            "Wariant a) List<Thread>\n" +
            "Wariant b) ThreadPool.QueueUserWorkItem",
            "O programie",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Zezwól na wpisywanie tylko cyfr w polu iteracji
    private void TxtIterations_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^\d+$");
    }
}
