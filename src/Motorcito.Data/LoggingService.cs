using Motorcito.Obd;

namespace Motorcito.Data;

/// <summary>
/// The app's single entry point into persistence: owns the database, the
/// repositories, and the lifetime of the current <see cref="TripRecorder"/>.
///
/// Exists so the view model does not have to know about connection strings,
/// vehicle identity resolution, or recorder lifecycles — and so all of that
/// stays testable without MAUI.
/// </summary>
public sealed class LoggingService : IDisposable
{
    /// <summary>
    /// Single-user placeholder until accounts exist.
    ///
    /// Every table already carries <c>user_id</c>, so introducing real accounts
    /// means changing this value's source, not the schema. It is a constant in
    /// one place rather than a literal scattered across call sites precisely so
    /// that change stays small.
    /// </summary>
    public const string LocalUserId = "local-user";

    private readonly MotorcitoDatabase _db;
    private readonly IAltitudeProvider _altitude;

    public VehicleRepository Vehicles { get; }
    public TripRepository Trips { get; }
    public SampleRepository Samples { get; }

    /// <summary>Raw adapter exchanges, for diagnosing what a car actually replied.</summary>
    public ObdLogRepository ObdLog { get; }

    /// <summary>The recorder for the current connection, or null when not recording.</summary>
    public TripRecorder? Recorder { get; private set; }

    /// <summary>Vehicle resolved on the current connection, or null when not recording.</summary>
    public Vehicle? CurrentVehicle { get; private set; }

    public string DatabasePath => _db.Path;

    public LoggingService(string databasePath, IAltitudeProvider? altitude = null)
    {
        _db = new MotorcitoDatabase(databasePath);
        Vehicles = new VehicleRepository(_db);
        Trips = new TripRepository(_db);
        Samples = new SampleRepository(_db);
        ObdLog = new ObdLogRepository(_db, LocalUserId);
        _altitude = altitude ?? NullAltitudeProvider.Instance;
    }

    /// <summary>
    /// Closes trips left open by a previous unclean shutdown. Call once at
    /// startup, before any recording begins, so a killed session cannot leave
    /// permanently broken rows behind.
    /// </summary>
    /// <returns>How many trips were repaired.</returns>
    public int RecoverOrphanedTrips() => Trips.CloseOrphanedTrips(Samples);

    /// <summary>
    /// Begins recording for a freshly connected car: resolves the vehicle from
    /// its VIN and capability scan, then opens a recorder against it.
    ///
    /// The capability scan is stored on every connection because it can
    /// legitimately change — a module asleep during the previous scan reports
    /// more PIDs the next time.
    /// </summary>
    public TripRecorder StartRecording(string? vin, SupportedPids supported, TripRecorderOptions? options = null)
    {
        CurrentVehicle = Vehicles.ResolveOrCreate(LocalUserId, vin, supported.ToJson());

        Recorder = new TripRecorder(Trips, Samples, LocalUserId, CurrentVehicle.VehicleId, options, _altitude);
        return Recorder;
    }

    /// <summary>Feeds a poller snapshot to the active recorder. A no-op when not recording.</summary>
    public void Record(ObdSnapshot snapshot) => Recorder?.Record(snapshot);

    /// <summary>Ends the current trip and stops recording.</summary>
    public void StopRecording()
    {
        Recorder?.OnDisconnected();
        Recorder = null;
        CurrentVehicle = null;
    }

    /// <summary>
    /// Writes any buffered samples immediately.
    ///
    /// Must be called when the app is backgrounded or told it is about to be
    /// terminated. Batching keeps up to <see cref="TripRecorderOptions.BatchSize"/>
    /// rows in memory, and iOS can kill a background app without warning, so
    /// this is what stops a lifecycle event costing real data.
    /// </summary>
    public int Flush()
    {
        // The exchange log is buffered too, and a diagnostic session is
        // worthless if the exchange that explains the failure is still in
        // memory when the app is killed.
        ObdLog.Flush();
        return Recorder?.Flush() ?? 0;
    }

    /// <summary>
    /// Writes a portable snapshot of the database for off-device analysis.
    ///
    /// Flushes first. Without that the export would omit whatever is still
    /// buffered in memory — which is always the most recent driving, and
    /// therefore exactly what you opened the file to look at.
    /// </summary>
    /// <returns>The path written.</returns>
    public string ExportDatabase(string destinationPath)
    {
        Flush();
        return DataExporter.SnapshotDatabase(_db, destinationPath);
    }

    /// <summary>
    /// Writes <c>trips.csv</c> and <c>samples.csv</c> into a directory, for
    /// starting an analysis in a spreadsheet or dataframe rather than SQL.
    /// </summary>
    /// <returns>The paths written, trips first.</returns>
    public IReadOnlyList<string> ExportCsv(string directory, string? vehicleId = null)
    {
        Flush();
        Directory.CreateDirectory(directory);

        var tripsPath = Path.Combine(directory, "trips.csv");
        var samplesPath = Path.Combine(directory, "samples.csv");

        using (var writer = new StreamWriter(tripsPath))
            DataExporter.WriteTripsCsv(_db, writer, vehicleId);

        using (var writer = new StreamWriter(samplesPath))
            DataExporter.WriteSamplesCsv(_db, writer, vehicleId);

        return [tripsPath, samplesPath];
    }

    /// <summary>
    /// Writes the raw adapter exchange log to a CSV file.
    ///
    /// Separate from the driving exports: it answers "what did the adapter
    /// actually say", and it contains raw replies including the VIN, so it is
    /// shared deliberately rather than bundled in by default.
    /// </summary>
    /// <returns>The path written, and how many exchanges it holds.</returns>
    public (string Path, int Rows) ExportDiagnosticLog(string destinationPath)
    {
        ObdLog.Flush();

        using var writer = new StreamWriter(destinationPath);
        var rows = DataExporter.WriteObdLogCsv(_db, writer);

        return (destinationPath, rows);
    }

    /// <summary>
    /// Deletes everything held about a user, for right-to-erasure requests.
    ///
    /// Stops any active recording first: erasing a user's vehicles while a
    /// recorder still holds their vehicle id would let the next flush insert
    /// rows that had just been deleted.
    /// </summary>
    public ErasureReport EraseUser(string userId = LocalUserId)
    {
        StopRecording();
        return DataErasure.EraseUser(_db, userId);
    }

    /// <summary>Rows still held for a user. Zero after <see cref="EraseUser"/>.</summary>
    public int CountRowsForUser(string userId = LocalUserId)
        => DataErasure.CountRowsForUser(_db, userId);

    public void Dispose()
    {
        // Ordering matters: the trip must be closed and its rows written before
        // the connection goes away.
        StopRecording();
        _db.Dispose();
    }
}
