using System.Globalization;
using Microsoft.Data.Sqlite;
using Motorcito.Data;
using Motorcito.Obd;

namespace Motorcito.Data.Tests;

public class DataExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"motorcito-export-{Guid.NewGuid():N}");

    public DataExporterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>Logs a short drive with known values, leaving the trip open.</summary>
    private static void SeedDrive(LoggingService logging, int samples = 10, double speed = 60)
    {
        var supported = SupportedPids.FromPids([0x05, 0x06, 0x0C, 0x0D]);
        logging.StartRecording("WBS8M9C50J5K12345", supported, new TripRecorderOptions { BatchSize = 5 });

        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < samples; i++)
        {
            logging.Record(new ObdSnapshot(t0.AddSeconds(i * 10), new Dictionary<byte, PidReading>
            {
                [0x0C] = new(PidRegistry.Find(0x0C)!, 2000, []),
                [0x0D] = new(PidRegistry.Find(0x0D)!, speed, []),
                [0x05] = new(PidRegistry.Find(0x05)!, 88, []),
                [0x06] = new(PidRegistry.Find(0x06)!, -3.5, [])
            }, 1));
        }
    }

    [Fact]
    public void Snapshot_includes_samples_still_buffered_in_memory()
    {
        // The failure this guards against: batching holds rows in memory, so a
        // naive export omits the most recent driving — the part you opened the
        // file to look at.
        using var logging = new LoggingService(Path.Combine(_directory, "live.db"));

        var supported = SupportedPids.FromPids([0x0C]);
        logging.StartRecording("VIN123", supported,
            new TripRecorderOptions { BatchSize = 1000, MaxBatchAge = TimeSpan.FromHours(1) });

        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 20; i++)
        {
            logging.Record(new ObdSnapshot(t0.AddSeconds(i), new Dictionary<byte, PidReading>
            {
                [0x0C] = new(PidRegistry.Find(0x0C)!, 2000, [])
            }, 1));
        }

        var exported = logging.ExportDatabase(Path.Combine(_directory, "snapshot.db"));

        Assert.Equal(20, CountSamples(exported));
    }

    [Fact]
    public void Snapshot_includes_rows_still_in_the_write_ahead_log()
    {
        // The second way an export goes quietly incomplete: WAL mode means
        // committed rows can live outside the main database file. Copying
        // motorcito.db alone would lose them.
        var livePath = Path.Combine(_directory, "wal.db");
        using var logging = new LoggingService(livePath);
        SeedDrive(logging, samples: 30);
        logging.StopRecording();

        // Confirm the premise: the WAL is genuinely holding data.
        Assert.True(new FileInfo($"{livePath}-wal").Length > 0);

        var exported = logging.ExportDatabase(Path.Combine(_directory, "snapshot.db"));

        Assert.Equal(30, CountSamples(exported));
    }

    [Fact]
    public void Snapshot_is_a_single_self_contained_file()
    {
        using var logging = new LoggingService(Path.Combine(_directory, "live.db"));
        SeedDrive(logging);
        logging.StopRecording();

        var exported = logging.ExportDatabase(Path.Combine(_directory, "snapshot.db"));

        // No -wal or -shm sidecars to forget when sharing the file.
        Assert.True(File.Exists(exported));
        Assert.False(File.Exists($"{exported}-wal"));
        Assert.False(File.Exists($"{exported}-shm"));
    }

    [Fact]
    public void Snapshot_replaces_a_previous_export_rather_than_merging()
    {
        using var logging = new LoggingService(Path.Combine(_directory, "live.db"));
        SeedDrive(logging, samples: 10);
        logging.StopRecording();

        var path = Path.Combine(_directory, "snapshot.db");
        logging.ExportDatabase(path);
        var second = logging.ExportDatabase(path);

        Assert.Equal(10, CountSamples(second));
    }

    [Fact]
    public void Exporting_does_not_disturb_an_ongoing_recording()
    {
        using var logging = new LoggingService(Path.Combine(_directory, "live.db"));
        SeedDrive(logging, samples: 10);

        var tripId = logging.Recorder!.CurrentTripId;
        logging.ExportDatabase(Path.Combine(_directory, "snapshot.db"));

        // Exporting mid-drive must not end the trip or drop the recorder.
        Assert.Equal(tripId, logging.Recorder!.CurrentTripId);

        logging.Record(new ObdSnapshot(DateTime.UtcNow, new Dictionary<byte, PidReading>
        {
            [0x0C] = new(PidRegistry.Find(0x0C)!, 2000, [])
        }, 1));
        logging.StopRecording();

        Assert.NotNull(logging.Trips.Get(tripId!)!.EndedAt);
    }

    [Fact]
    public void Csv_export_writes_both_files_with_headers()
    {
        using var logging = new LoggingService(Path.Combine(_directory, "live.db"));
        SeedDrive(logging, samples: 12);
        logging.StopRecording();

        var files = logging.ExportCsv(Path.Combine(_directory, "csv"));

        Assert.Equal(2, files.Count);
        var trips = File.ReadAllLines(files[0]);
        var samples = File.ReadAllLines(files[1]);

        Assert.StartsWith("trip_id,vehicle_id,started_at", trips[0]);
        Assert.Equal(2, trips.Length);                  // header + one trip
        Assert.StartsWith("trip_id,vehicle_id,ts", samples[0]);
        Assert.Equal(13, samples.Length);               // header + 12 samples
    }

    [Fact]
    public void Csv_uses_invariant_numbers_regardless_of_locale()
    {
        // Under es-ES the default formatter renders 3.5 as "3,5", which in a
        // comma-separated file silently becomes two columns and shifts every
        // field after it. The export must not depend on regional settings.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");

            using var logging = new LoggingService(Path.Combine(_directory, "live.db"));
            SeedDrive(logging, samples: 3);
            logging.StopRecording();

            var files = logging.ExportCsv(Path.Combine(_directory, "csv"));
            var samples = File.ReadAllLines(files[1]);

            // stft1 was logged as -3.5 and must survive as a decimal point.
            Assert.Contains("-3.5", samples[1]);
            Assert.DoesNotContain("-3,5", samples[1]);

            // Column count must match the header on every row.
            var columns = samples[0].Split(',').Length;
            Assert.All(samples.Skip(1), line => Assert.Equal(columns, line.Split(',').Length));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Csv_leaves_unread_pids_empty_rather_than_zero()
    {
        using var logging = new LoggingService(Path.Combine(_directory, "live.db"));
        SeedDrive(logging, samples: 3);
        logging.StopRecording();

        var files = logging.ExportCsv(Path.Combine(_directory, "csv"));
        var header = File.ReadAllLines(files[1])[0].Split(',');
        var row = File.ReadAllLines(files[1])[1].Split(',');

        // maf_gps was never reported by this car; a zero here would be averaged
        // into any analysis as though the engine were drawing no air.
        var maf = Array.IndexOf(header, "maf_gps");
        Assert.Equal(string.Empty, row[maf]);
    }

    [Fact]
    public void Csv_can_be_filtered_to_one_vehicle()
    {
        using var logging = new LoggingService(Path.Combine(_directory, "live.db"));
        SeedDrive(logging, samples: 5);
        logging.StopRecording();
        var first = logging.Vehicles.All(LoggingService.LocalUserId).Single().VehicleId;

        // A second car on the same phone.
        logging.StartRecording("JM1NDAD75M0123456", SupportedPids.FromPids([0x0C]),
            new TripRecorderOptions { BatchSize = 1 });
        logging.Record(new ObdSnapshot(DateTime.UtcNow, new Dictionary<byte, PidReading>
        {
            [0x0C] = new(PidRegistry.Find(0x0C)!, 900, [])
        }, 1));
        logging.StopRecording();

        var files = logging.ExportCsv(Path.Combine(_directory, "csv"), first);

        Assert.Equal(2, File.ReadAllLines(files[0]).Length);   // header + the first car's trip only
        Assert.All(File.ReadAllLines(files[1]).Skip(1), line => Assert.Contains(first, line));
    }

    private static int CountSamples(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM samples;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
