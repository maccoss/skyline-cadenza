using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SkylineCadenza.Core.Ingest;
using SkylineCadenza.Core.Parsimony;
using SkylineCadenza.Core.Scheduling;
using SkylineCadenza.Core.SkylineRpc;
using CoverageCurves = SkylineCadenza.Core.Scheduling.CoverageCurves;

namespace SkylineCadenza.App.ViewModels;

/// <summary>
/// Root view model: input file paths, scheduling parameters (live-bound),
/// async commands to ingest + schedule, and an observable
/// <see cref="ScheduleResult"/> that the plot views render from.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private string? _reportPath;
    [ObservableProperty] private string? _carafeTsvPath;

    // Acquisition + load balancing
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlotsLabel))]
    private AcquisitionMode _mode = AcquisitionMode.Mtm;

    /// <summary>Header text for the slots metric: "MTM slots" or "PRM slots".</summary>
    public string SlotsLabel => Mode == AcquisitionMode.Prm ? "PRM slots" : "MTM slots";
    [ObservableProperty] private bool _enableLoadBalancing = true;

    // MTM constraints
    [ObservableProperty] private double _isolationWindowTh = 3.0;
    [ObservableProperty] private double _fragmentTolDa = 0.5;

    // PRM quad isolation width (also the minimum slot width for solo MTM slots).
    [ObservableProperty] private double _prmIsolationWidthTh = 0.7;

    // Per-slot charge constraint + Thermo CSV NCE.
    [ObservableProperty] private ChargeHandling _chargeHandling = ChargeHandling.SameChargePerSlot;
    [ObservableProperty] private double _normalizedCollisionEnergy = 28.0;

    // Cycle budget
    [ObservableProperty] private int _cycleBudget = 100;
    [ObservableProperty] private double _firingPadSec = 15.0;

    // Library filter
    [ObservableProperty] private double _qValueCutoff = 0.01;

    // Per-protein bounds + priority knobs
    [ObservableProperty] private int _minPeptidesPerProtein = 1;
    [ObservableProperty] private int _maxPeptidesPerProtein = 5;
    [ObservableProperty] private ProteinPriority _proteinRanking = ProteinPriority.SummedIntensity;
    [ObservableProperty] private PeptidePriority _peptideRanking = PeptidePriority.PrecursorQuantity;

    // Target protein list
    [ObservableProperty] private string _targetProteinsText = string.Empty;
    [ObservableProperty] private TargetListMode _targetMode = TargetListMode.FirstThenFill;
    [ObservableProperty] private int _targetProteinsParsedCount;

    // Status + result
    [ObservableProperty] private string _statusMessage = "Open a DIA-NN report.parquet to begin.";
    [ObservableProperty] private bool _isBusy;

    // Library-level stats (everything that passed the q-value filter in the
    // report, before scheduling).
    [ObservableProperty] private int _libraryPrecursors;
    [ObservableProperty] private int _libraryPeptides;
    [ObservableProperty] private int _libraryProteinGroups;
    [ObservableProperty] private int _libraryCarafeMatched;

    // Heatmap view toggle.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeatmapTitle))]
    private HeatmapView _heatmapView = HeatmapView.FullLibrary;

    public string HeatmapTitle => HeatmapView == HeatmapView.FullLibrary
        ? "Precursors per spectrum (full library)"
        : "Precursors per spectrum (scheduled assay)";

    [ObservableProperty] private ScheduleResult? _scheduleResult;
    [ObservableProperty] private int _scheduledPrecursors;
    [ObservableProperty] private int _scheduledPeptides;
    [ObservableProperty] private int _proteinsCovered;
    [ObservableProperty] private int _slotsTotal;
    [ObservableProperty] private int _slotsShared;
    [ObservableProperty] private int _peakLoad;
    [ObservableProperty] private double _medianPeptidesPerProtein;

    /// <summary>Per-protein summary used by the protein-coverage plot.</summary>
    public List<ProteinCoverageRow> ProteinCoverage { get; } = new();

    /// <summary>Per-run coverage curves (one per distinct DIA-NN run name).</summary>
    public CoverageCurves.CurveSeries[] PerRunCurves { get; private set; } = Array.Empty<CoverageCurves.CurveSeries>();

    /// <summary>m/z x RT heatmap of the full library.</summary>
    public CoverageCurves.Heatmap? FullLibraryHeatmap { get; private set; }

    /// <summary>m/z x RT heatmap of just the scheduled precursors.</summary>
    public CoverageCurves.Heatmap? ScheduledHeatmap { get; private set; }

    /// <summary>The heatmap currently selected by <see cref="HeatmapView"/>.</summary>
    public CoverageCurves.Heatmap? Heatmap =>
        HeatmapView == HeatmapView.FullLibrary ? FullLibraryHeatmap : ScheduledHeatmap;

    /// <summary>Raised after a successful reschedule so views refresh.</summary>
    public event Action? ScheduleUpdated;

    /// <summary>Raised after a fresh ingest. Library-level plots refresh from this.</summary>
    public event Action? LibraryLoaded;

    private List<Candidate>? _candidates;
    private CancellationTokenSource? _rescheduleCts;
    private Dictionary<string, IReadOnlyList<string>>? _geneToAccession;
    private HashSet<string>? _knownAccessions;
    private SkylineSession? _skylineSession;
    private System.Threading.Timer? _skylinePoll;
    private int _skylinePollFailures;
    private CancellationTokenSource? _skylineLoadCts;

    /// <summary>True while a long Skyline operation is interruptible via <see cref="CancelSkylineLoadCommand"/>.</summary>
    [ObservableProperty] private bool _skylineLoadCancellable;

    [RelayCommand]
    private void CancelSkylineLoad()
    {
        _skylineLoadCts?.Cancel();
        StatusMessage = "Skyline load cancellation requested...";
        SkylineStatus = "Skyline: load cancellation requested.";
    }

    // Skyline connection state (bound to the toolbar in the UI).
    [ObservableProperty] private bool _skylineConnected;
    [ObservableProperty] private string _skylineStatus = "Skyline: not connected.";
    [ObservableProperty] private string? _skylineDocumentPath;

    public MainViewModel()
    {
        // Try to attach to a running Skyline instance on launch. Done on a
        // background thread because NamedPipeClientStream.Connect() blocks
        // for up to 5 s during discovery; running it inline would freeze
        // the splash.
        _ = Task.Run(TryConnectSkyline);
    }

    private void TryConnectSkyline()
    {
        try
        {
            _skylineSession = SkylineSession.FromArguments(App.LaunchArgs);
            // Don't open the pipe yet - Skyline closes it after each
            // request, so we connect lazily inside each Execute() call.
            // Kick off a 2 s polling loop to track document changes.
            _skylinePoll = new System.Threading.Timer(_ => PollSkyline(),
                null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SkylineConnected = false;
                SkylineStatus = $"Skyline: not connected ({ex.GetType().Name}: {ex.Message}).";
            });
        }
    }

    private void PollSkyline()
    {
        if (_skylineSession is null) return;
        try
        {
            // Connect-per-call: each Skyline RPC opens a fresh pipe and
            // disposes after the response. Reusing a single pipe across
            // calls fails because Skyline closes its side between requests.
            var st = _skylineSession.Execute(c => c.GetDocumentStatus());
            _skylinePollFailures = 0;
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                SkylineConnected = true;
                SkylineDocumentPath = st?.DocumentPath;
                SkylineStatus = st is null
                    ? "Skyline: connected, no document open."
                    : $"Skyline: {st.DocumentPath ?? "(unsaved)"} - {st.Molecules:n0} peptides, {st.Precursors:n0} precursors.";
            });
        }
        catch (System.Text.Json.JsonException jex)
        {
            // Most often this means Skyline sent back an empty or
            // truncated message - it happens transiently when the pipe is
            // mid-handshake. Surface the exact reader message so we can
            // tell apart "empty response" from "unexpected token".
            _skylinePollFailures++;
            if (_skylinePollFailures >= 3)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    SkylineConnected = false;
                    SkylineStatus = $"Skyline: response not parseable as JSON ({jex.Message}).";
                });
            }
        }
        catch (Exception ex)
        {
            // Skyline closes pipes between requests, so a transient
            // IOException right after a successful poll is normal. Only
            // flip to "connection lost" after a couple of failures.
            _skylinePollFailures++;
            if (_skylinePollFailures >= 3)
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    SkylineConnected = false;
                    SkylineStatus = $"Skyline: connection lost ({ex.GetType().Name}: {ex.Message}).";
                });
            }
        }
    }

    [RelayCommand]
    private void BrowseReport()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "DIA-NN report (*.parquet)|*.parquet|All files (*.*)|*.*",
            Title = "Select DIA-NN report.parquet",
        };
        if (dlg.ShowDialog() == true)
            ReportPath = dlg.FileName;
    }

    [RelayCommand]
    private void BrowseCarafe()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Carafe library (*.tsv)|*.tsv|All files (*.*)|*.*",
            Title = "Select Carafe spectral library TSV",
        };
        if (dlg.ShowDialog() == true)
            CarafeTsvPath = dlg.FileName;
    }

    [RelayCommand]
    private void ExportThermoCsv()
    {
        if (_candidates is null || ScheduleResult is null)
        {
            StatusMessage = "Schedule something first - no targets to export.";
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Thermo inclusion list (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = "cadenza_targets.csv",
            Title = "Export Thermo scheduled inclusion CSV",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var csv = ThermoCsvWriter.Build(_candidates, ScheduleResult, CurrentParameters());
            File.WriteAllText(dlg.FileName, csv);
            StatusMessage = $"Wrote {dlg.FileName}. Import this in the Thermo Method Editor (Import Scheduled List).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to write CSV: {ex.Message}";
        }
    }

    [RelayCommand]
    private void UpdateSkyline()
    {
        if (_candidates is null || ScheduleResult is null)
        {
            StatusMessage = "Schedule something first - no targets to push.";
            return;
        }
        if (_skylineSession is null)
        {
            StatusMessage = "Skyline: not connected. Launch this tool from Skyline's Tools menu.";
            return;
        }
        // InsertSmallMoleculeTransitionList puts rows into the document's
        // small-molecule subtree, which is invisible in a proteomics
        // document. For peptide targets we write a peptide-style CSV to a
        // temp file and call SkylineCmd --import-transition-list directly
        // via RunCommand; that hits the same code path Skyline uses for
        // the UI's Edit > Insert > Transition List flow and lands rows in
        // the protein/peptide tree.
        string? tempPath = null;
        try
        {
            StatusMessage = "Pushing scheduled targets to Skyline...";
            var csv = PeptideTransitionListBuilder.Build(_candidates, ScheduleResult);
            tempPath = Path.Combine(Path.GetTempPath(),
                $"cadenza-targets-{Guid.NewGuid():N}.csv");
            File.WriteAllText(tempPath, csv);
            string output = _skylineSession.Execute(c => c.RunCommand(new[]
            {
                "--import-transition-list=" + tempPath,
            }));
            string head = string.IsNullOrWhiteSpace(output)
                ? "(no output)"
                : output.Length > 400 ? output.Substring(0, 400) + "..." : output;
            StatusMessage = $"Pushed {ScheduleResult.ScheduledIndices.Length:n0} precursors via --import-transition-list. "
                + $"Skyline: {head}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Skyline import failed ({ex.GetType().Name}): {ex.Message}";
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
        }
    }

    [RelayCommand]
    private async Task LoadFromSkylineAsync()
    {
        if (_skylineSession is null)
        {
            StatusMessage = "Skyline: not connected. Launch this tool from Skyline's Tools menu.";
            return;
        }
        IsBusy = true;
        SkylineLoadCancellable = true;
        var pollWasRunning = _skylinePoll;
        _skylinePoll?.Dispose();
        _skylinePoll = null;
        _skylineLoadCts?.Dispose();
        _skylineLoadCts = new CancellationTokenSource();
        var ct = _skylineLoadCts.Token;

        try
        {
            var progress = new Progress<string>(msg =>
            {
                StatusMessage = msg;
                SkylineStatus = msg;
            });
            var loadResult = await SkylineLibraryLoader.LoadAsync(_skylineSession!, progress: progress, cancellationToken: ct);
            if (loadResult.Candidates.Count == 0)
            {
                string empty = "Skyline: no candidates built from the document. "
                    + $"Probed RT column '{loadResult.RtColumnUsed}', fetched {loadResult.RawRowsFetched:n0} rows.";
                StatusMessage = empty;
                SkylineStatus = empty;
                return;
            }

            _candidates = loadResult.Candidates;
            _knownAccessions = _candidates.Select(c => c.ProteinGroup).ToHashSet();
            _geneToAccession = new Dictionary<string, IReadOnlyList<string>>();

            PerRunCurves = await Task.Run(() => CoverageCurves.PerRunCurves(_candidates));
            FullLibraryHeatmap = await Task.Run(() => CoverageCurves.BuildHeatmap(_candidates));

            LibraryPrecursors = _candidates.Count;
            LibraryPeptides = _candidates.Select(c => c.StrippedSequence).Distinct().Count();
            LibraryProteinGroups = _knownAccessions.Count;
            LibraryCarafeMatched = _candidates.Count(c => c.Top4Fragments.Length > 0);

            LibraryLoaded?.Invoke();
            string ok = $"Skyline document loaded: {LibraryPrecursors:n0} precursors / {LibraryPeptides:n0} peptides / {LibraryProteinGroups:n0} groups (RT col '{loadResult.RtColumnUsed}', {loadResult.RawRowsFetched:n0} rows).";
            StatusMessage = ok;
            SkylineStatus = ok;
            await RescheduleAsync();
            return;
        }
        catch (OperationCanceledException)
        {
            string cancelled = "Skyline: document load cancelled.";
            StatusMessage = cancelled;
            SkylineStatus = cancelled;
            return;
        }
        catch (Exception ex)
        {
            string err = $"Skyline document load failed: {ex.GetType().Name}: {ex.Message}";
            StatusMessage = err;
            SkylineStatus = err;
            return;
        }
        finally
        {
            IsBusy = false;
            SkylineLoadCancellable = false;
            _skylineLoadCts?.Dispose();
            _skylineLoadCts = null;
            if (pollWasRunning is not null)
            {
                _skylinePoll = new System.Threading.Timer(_ => PollSkyline(),
                    null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            }
        }

        // Legacy probe path below for reference - unreachable because of the return above.
#pragma warning disable CS0162
        var unused = _skylinePoll;
        var pollWasRunningLegacy = _skylinePoll;
        try
        {
            // Probe a sequence of built-in report names with a per-call
            // timeout. Reports on a 72k-precursor document can take many
            // seconds to build server-side, so the timeout exists to keep
            // the UI responsive, not to give up on real work.
            string[] candidates =
            {
                "Peptide Transition List",
                "Transition Results",
                "Peptide Ratio Results",
                "Molecule Transition List",
            };
            var perProbeTimeout = TimeSpan.FromSeconds(30);

            SkylineTool.ReportRowsResult? firstHit = null;
            string? firstHitName = null;
            foreach (var name in candidates)
            {
                string msg = $"Skyline: fetching '{name}' (timeout {perProbeTimeout.TotalSeconds:0}s)...";
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    StatusMessage = msg;
                    SkylineStatus = msg;
                });
                try
                {
                    var fetch = Task.Run(() => _skylineSession.Execute(c => c.GetReportRows(
                        reportName: name,
                        offset: 0,
                        count: 50,
                        columns: null,
                        filter: null,
                        includeMaxLength: false,
                        culture: "invariant")));
                    var winner = await Task.WhenAny(fetch, Task.Delay(perProbeTimeout));
                    if (winner != fetch)
                    {
                        string slow = $"Skyline: '{name}' didn't respond in {perProbeTimeout.TotalSeconds:0}s. Trying next.";
                        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        {
                            StatusMessage = slow;
                            SkylineStatus = slow;
                        });
                        continue;
                    }
                    var result = await fetch;
                    if (result?.TotalRows > 0)
                    {
                        firstHit = result;
                        firstHitName = name;
                        break;
                    }
                    string empty = $"Skyline: '{name}' returned 0 rows. Trying next.";
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        StatusMessage = empty;
                        SkylineStatus = empty;
                    });
                }
                catch (Exception probeEx)
                {
                    string rej = $"Skyline: '{name}' rejected ({probeEx.GetType().Name}: {probeEx.Message}). Trying next.";
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        StatusMessage = rej;
                        SkylineStatus = rej;
                    });
                }
            }

            if (firstHit is null)
            {
                string fail = "Skyline: no built-in report returned data. Open Tools > Reports in Skyline "
                    + "and tell me the exact report name + columns I should map.";
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    StatusMessage = fail;
                    SkylineStatus = fail;
                });
                return;
            }

            int cols = firstHit.Columns?.Length ?? 0;
            var colNames = firstHit.Columns is null
                ? "(no columns)"
                : string.Join(", ", firstHit.Columns.Select(c => c.Name));
            string ok = $"Skyline: '{firstHitName}' -> {firstHit.TotalRows:n0} rows / {cols} cols. "
                + $"Columns: {colNames}. "
                + "Send me this column list and the candidate builder will be wired against it.";
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                StatusMessage = ok;
                SkylineStatus = ok;
            });
        }
        catch (Exception ex)
        {
            string err = $"Skyline report fetch failed: {ex.GetType().Name}: {ex.Message}";
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                StatusMessage = err;
                SkylineStatus = err;
            });
        }
        finally
        {
            IsBusy = false;
            // Resume the background poll loop.
            if (pollWasRunningLegacy is not null)
            {
                _skylinePoll = new System.Threading.Timer(_ => PollSkyline(),
                    null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            }
        }
#pragma warning restore CS0162
    }

    [RelayCommand]
    private void BrowseTargetList()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "FASTA (*.fasta;*.fa)|*.fasta;*.fa|Text list (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*",
            Title = "Open target protein list (FASTA or accession/gene list)",
        };
        if (dlg.ShowDialog() == true)
        {
            try { TargetProteinsText = File.ReadAllText(dlg.FileName); }
            catch (Exception ex) { StatusMessage = $"Could not read {dlg.FileName}: {ex.Message}"; }
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        bool hasReport = !string.IsNullOrEmpty(ReportPath) && File.Exists(ReportPath);
        bool hasCarafe = !string.IsNullOrEmpty(CarafeTsvPath) && File.Exists(CarafeTsvPath);
        bool hasTargets = !string.IsNullOrWhiteSpace(TargetProteinsText);

        if (!hasReport && !(hasCarafe && hasTargets))
        {
            StatusMessage = "Need either a DIA-NN report.parquet, OR a Carafe library + a target protein list.";
            return;
        }

        IsBusy = true;
        try
        {
            if (hasReport) await LoadFromDiannAsync();
            else await LoadFromCarafeOnlyAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadFromDiannAsync()
    {
        StatusMessage = "Reading DIA-NN report...";
        var rows = await DiannParquetReader.LoadAsync(ReportPath!, QValueCutoff);
        StatusMessage = $"Loaded {rows.Count:n0} precursors. Running parsimony...";

        var pepMap = DiannParquetReader.BuildPeptideProteinMap(rows);
        var parsimony = ParsimonyEngine.Assign(pepMap);

        _geneToAccession = ProteinListParser.BuildGeneToAccession(rows);
        _knownAccessions = parsimony.Values.Select(v => v.Group).ToHashSet();

        var frags = new Dictionary<FragmentKey, double[]>();
        if (!string.IsNullOrEmpty(CarafeTsvPath) && File.Exists(CarafeTsvPath))
        {
            StatusMessage = $"Streaming Carafe library {Path.GetFileName(CarafeTsvPath)}...";
            var allowed = new HashSet<string>(rows.Select(r => CarafeKey.FromDiann(r.ModifiedSequence)));
            frags = await Task.Run(() => CarafeTsvReader.ExtractTopFragments(CarafeTsvPath!, allowed, 4));
        }

        _candidates = CandidateBuilder.Build(rows, parsimony, frags);
        await FinalizeLoadAsync(
            fragSourceLabel: frags.Count > 0 ? "Carafe library" : "DIA-NN report fragments",
            sourceLabel: "DIA-NN report");
    }

    private async Task LoadFromCarafeOnlyAsync()
    {
        StatusMessage = "Parsing target protein list...";
        var tokens = ProteinListParser.ParseText(TargetProteinsText);
        var allowed = new HashSet<string>(tokens);
        if (allowed.Count == 0)
        {
            StatusMessage = "Target protein list is empty - cannot load Carafe-only mode.";
            return;
        }

        StatusMessage = $"Streaming Carafe library for {allowed.Count:n0} target accessions...";
        var carafePrecursors = await Task.Run(() =>
            CarafeLibraryReader.LoadCandidates(CarafeTsvPath!, allowed));
        if (carafePrecursors.Count == 0)
        {
            StatusMessage = "No Carafe precursors matched the target protein list. Check the accession format.";
            return;
        }

        StatusMessage = $"Loaded {carafePrecursors.Count:n0} Carafe precursors. Running parsimony...";
        var pepMap = CandidateBuilder.CarafePeptideProteinMap(carafePrecursors);
        var parsimony = ParsimonyEngine.Assign(pepMap);

        _geneToAccession = new Dictionary<string, IReadOnlyList<string>>();
        _knownAccessions = parsimony.Values.Select(v => v.Group).ToHashSet();

        _candidates = CandidateBuilder.BuildFromCarafe(carafePrecursors, parsimony);
        await FinalizeLoadAsync(
            fragSourceLabel: "Carafe predicted fragments",
            sourceLabel: "Carafe library + target list");
    }

    private async Task FinalizeLoadAsync(string fragSourceLabel, string sourceLabel)
    {
        if (_candidates is null) return;

        PerRunCurves = await Task.Run(() => CoverageCurves.PerRunCurves(_candidates));
        FullLibraryHeatmap = await Task.Run(() => CoverageCurves.BuildHeatmap(_candidates));

        LibraryPrecursors = _candidates.Count;
        LibraryPeptides = _candidates.Select(c => c.StrippedSequence).Distinct().Count();
        LibraryProteinGroups = _knownAccessions?.Count ?? 0;
        LibraryCarafeMatched = _candidates.Count(c => c.Top4Fragments.Length > 0);

        LibraryLoaded?.Invoke();
        StatusMessage = $"Source: {sourceLabel}. Loaded {LibraryPrecursors:n0} precursors / {LibraryPeptides:n0} peptides / {LibraryProteinGroups:n0} groups. Fragments: {fragSourceLabel}.";
        await RescheduleAsync();
    }

    [RelayCommand]
    public async Task RescheduleAsync()
    {
        if (_candidates is null) return;
        _rescheduleCts?.Cancel();
        var cts = _rescheduleCts = new CancellationTokenSource();
        var p = CurrentParameters();
        try
        {
            var result = await Task.Run(() => Scheduler.Run(_candidates!, p, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            ApplyResult(result, p);
        }
        catch (System.OperationCanceledException) { }
    }

    private void ApplyResult(ScheduleResult result, SchedulingParameters p)
    {
        ScheduleResult = result;
        ScheduledPrecursors = result.ScheduledIndices.Length;
        SlotsTotal = result.Slots.Length;
        SlotsShared = result.Slots.Count(s => s.MemberIndices.Count > 1);
        PeakLoad = result.SlotCountCurve.Length == 0 ? 0 : result.SlotCountCurve.Max();
        ProteinsCovered = result.ProteinGroupsCovered;

        // Per-group rollups for stats + coverage plot.
        var scheduledByGroup = new Dictionary<string, List<int>>();
        for (int i = 0; i < result.ScheduledIndices.Length; i++)
        {
            int candIdx = result.ScheduledIndices[i];
            var g = _candidates![candIdx].ProteinGroup;
            if (!scheduledByGroup.TryGetValue(g, out var list))
            {
                list = new List<int>();
                scheduledByGroup[g] = list;
            }
            list.Add(candIdx);
        }

        // Distinct peptide-sequence count across scheduled precursors.
        var seenPep = new HashSet<string>();
        foreach (var idx in result.ScheduledIndices)
            seenPep.Add(_candidates![idx].StrippedSequence);
        ScheduledPeptides = seenPep.Count;

        // Median peptides per scheduled group (excluding 0-coverage).
        var perGroup = scheduledByGroup.Select(kv => kv.Value.Select(i => _candidates![i].StrippedSequence).Distinct().Count()).ToList();
        perGroup.Sort();
        MedianPeptidesPerProtein = perGroup.Count == 0 ? 0
            : perGroup.Count % 2 == 1 ? perGroup[perGroup.Count / 2]
            : (perGroup[perGroup.Count / 2 - 1] + perGroup[perGroup.Count / 2]) / 2.0;

        // Coverage plot data: one row per protein group with summed
        // peptide intensity across the entire library + peptides actually
        // scheduled. Sort descending by intensity for easy display.
        var groupIntensity = new Dictionary<string, double>();
        foreach (var c in _candidates!)
        {
            if (!groupIntensity.TryGetValue(c.ProteinGroup, out var sum)) sum = 0.0;
            groupIntensity[c.ProteinGroup] = sum + c.PrecursorQuantity;
        }
        ProteinCoverage.Clear();
        ProteinCoverage.AddRange(
            groupIntensity
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new ProteinCoverageRow(
                    kv.Key,
                    kv.Value,
                    scheduledByGroup.TryGetValue(kv.Key, out var idxs)
                        ? idxs.Select(i => _candidates![i].StrippedSequence).Distinct().Count()
                        : 0)));

        // Build the scheduled-only heatmap so the user can toggle between
        // "full library" and "what's actually in the assay".
        if (_candidates is { Count: > 0 } && result.ScheduledIndices.Length > 0)
        {
            var subset = new List<Candidate>(result.ScheduledIndices.Length);
            foreach (var idx in result.ScheduledIndices) subset.Add(_candidates[idx]);
            ScheduledHeatmap = CoverageCurves.BuildHeatmap(subset);
        }
        else
        {
            ScheduledHeatmap = null;
        }

        StatusMessage = $"Scheduled {ScheduledPrecursors:n0} precursors / {ScheduledPeptides:n0} peptides covering {ProteinsCovered:n0}/{_knownAccessions?.Count:n0} groups in {SlotsTotal:n0} slots (peak load {PeakLoad}/{p.CycleBudget}).";
        ScheduleUpdated?.Invoke();
    }

    partial void OnHeatmapViewChanged(HeatmapView value)
    {
        OnPropertyChanged(nameof(Heatmap));
        HeatmapViewChanged?.Invoke();
    }

    /// <summary>Fires when the heatmap view toggle changes, so the View can re-render.</summary>
    public event Action? HeatmapViewChanged;

    private SchedulingParameters CurrentParameters()
    {
        var targets = new HashSet<string>();
        if (!string.IsNullOrWhiteSpace(TargetProteinsText) && _knownAccessions is not null && _geneToAccession is not null)
        {
            var tokens = ProteinListParser.ParseText(TargetProteinsText);
            var resolved = ProteinListParser.ResolveGenes(tokens, _geneToAccession, _knownAccessions);
            foreach (var t in resolved) targets.Add(t);
        }
        TargetProteinsParsedCount = targets.Count;
        return new SchedulingParameters
        {
            Mode = Mode,
            EnableLoadBalancing = EnableLoadBalancing,
            IsolationWindowTh = IsolationWindowTh,
            FragmentTolDa = FragmentTolDa,
            CycleBudget = CycleBudget,
            FiringPadSec = FiringPadSec,
            QValueCutoff = QValueCutoff,
            PrmIsolationWidthTh = PrmIsolationWidthTh,
            ChargeHandling = ChargeHandling,
            NormalizedCollisionEnergy = NormalizedCollisionEnergy,
            TargetProteins = targets,
            TargetMode = TargetMode,
            MinPeptidesPerProtein = MinPeptidesPerProtein,
            MaxPeptidesPerProtein = MaxPeptidesPerProtein,
            ProteinRanking = ProteinRanking,
            PeptideRanking = PeptideRanking,
        };
    }

    // Parameter-change hooks: each one debounces by simply firing a fresh
    // reschedule. RescheduleAsync cancels prior runs, so the slider can
    // move freely without queuing stale work.
    partial void OnModeChanged(AcquisitionMode value) => _ = RescheduleAsync();
    partial void OnEnableLoadBalancingChanged(bool value) => _ = RescheduleAsync();
    partial void OnIsolationWindowThChanged(double value) => _ = RescheduleAsync();
    partial void OnFragmentTolDaChanged(double value) => _ = RescheduleAsync();
    partial void OnCycleBudgetChanged(int value) => _ = RescheduleAsync();
    partial void OnFiringPadSecChanged(double value) => _ = RescheduleAsync();
    partial void OnPrmIsolationWidthThChanged(double value) => _ = RescheduleAsync();
    partial void OnChargeHandlingChanged(ChargeHandling value) => _ = RescheduleAsync();
    // NormalizedCollisionEnergy is output-only - no reschedule.
    partial void OnMinPeptidesPerProteinChanged(int value) => _ = RescheduleAsync();
    partial void OnMaxPeptidesPerProteinChanged(int value) => _ = RescheduleAsync();
    partial void OnProteinRankingChanged(ProteinPriority value) => _ = RescheduleAsync();
    partial void OnPeptideRankingChanged(PeptidePriority value) => _ = RescheduleAsync();
    partial void OnTargetModeChanged(TargetListMode value) => _ = RescheduleAsync();
    partial void OnTargetProteinsTextChanged(string value) => _ = RescheduleAsync();
}

/// <summary>One row in the protein-coverage plot's data series.</summary>
public sealed record ProteinCoverageRow(string ProteinGroup, double SummedIntensity, int PeptidesScheduled);

/// <summary>Which precursor set drives the m/z x RT heatmap.</summary>
public enum HeatmapView
{
    /// <summary>Every precursor in the loaded DIA-NN report.</summary>
    FullLibrary,
    /// <summary>Only precursors selected by the current schedule.</summary>
    ScheduledAssay,
}
