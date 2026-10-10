using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Tally;

public sealed record UsageEvent(string CustomerId, string EventId, string Meter, decimal Quantity, DateTime EventTime);

public sealed record IngestResult(int Received, int Accepted, int Duplicates, int Rejected);

public sealed record InvoiceLine(int LineNo, string Kind, string Period, string? Meter, string Description, decimal Quantity, decimal Amount);

public sealed record Invoice(long Id, string CustomerId, int Number, string Currency, decimal Total, DateTime CreatedAt, IReadOnlyList<InvoiceLine> Lines);

public sealed record UsageSummary(string Meter, string Period, decimal Quantity, long Events, decimal Cost, decimal Billed);

public sealed record BillingOutcome(Invoice? Invoice, IReadOnlyList<string> UnpricedMeters);

public sealed partial class TallyStore(NpgsqlDataSource dataSource)
{
    public const int MaxBatch = 5000;
    public const decimal MaxQuantity = 1_000_000_000_000m;

    internal static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,63}$")]
    private static partial Regex MeterName();

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS plans (
            id text NOT NULL,
            version int NOT NULL,
            body jsonb NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (id, version)
        );

        CREATE TABLE IF NOT EXISTS customers (
            id text PRIMARY KEY,
            plan_id text NOT NULL,
            plan_version int NOT NULL,
            start_date date NOT NULL,
            next_invoice_no int NOT NULL DEFAULT 1,
            created_at timestamptz NOT NULL DEFAULT now(),
            FOREIGN KEY (plan_id, plan_version) REFERENCES plans (id, version)
        );

        CREATE TABLE IF NOT EXISTS usage_events (
            customer_id text NOT NULL,
            event_id text NOT NULL,
            meter text NOT NULL,
            quantity numeric(30, 9) NOT NULL CHECK (quantity > 0),
            event_time timestamptz NOT NULL,
            period char(7) NOT NULL,
            received_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (customer_id, event_id)
        );
        CREATE INDEX IF NOT EXISTS usage_events_period ON usage_events (customer_id, period, meter);

        CREATE TABLE IF NOT EXISTS usage_rollups (
            customer_id text NOT NULL,
            meter text NOT NULL,
            period char(7) NOT NULL,
            quantity numeric(30, 9) NOT NULL,
            events bigint NOT NULL,
            billed_quantity numeric(30, 9) NOT NULL DEFAULT 0,
            billed_amount numeric(24, 6) NOT NULL DEFAULT 0,
            PRIMARY KEY (customer_id, meter, period)
        ) WITH (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.02);
        CREATE INDEX IF NOT EXISTS usage_rollups_period ON usage_rollups (customer_id, period);

        CREATE TABLE IF NOT EXISTS invoices (
            id bigserial PRIMARY KEY,
            customer_id text NOT NULL REFERENCES customers (id),
            number int NOT NULL,
            currency text NOT NULL,
            total numeric(24, 6) NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE (customer_id, number)
        );

        CREATE TABLE IF NOT EXISTS invoice_lines (
            invoice_id bigint NOT NULL REFERENCES invoices (id),
            line_no int NOT NULL,
            kind text NOT NULL,
            period char(7) NOT NULL,
            meter text,
            description text NOT NULL,
            quantity numeric(30, 9) NOT NULL,
            amount numeric(24, 6) NOT NULL,
            PRIMARY KEY (invoice_id, line_no)
        );

        CREATE TABLE IF NOT EXISTS base_fees_billed (
            customer_id text NOT NULL,
            period char(7) NOT NULL,
            invoice_id bigint NOT NULL,
            PRIMARY KEY (customer_id, period)
        );

        CREATE OR REPLACE FUNCTION tally_immutable() RETURNS trigger AS $$
        BEGIN
            RAISE EXCEPTION 'invoices are immutable; issue an adjustment instead';
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS invoices_immutable ON invoices;
        CREATE TRIGGER invoices_immutable BEFORE UPDATE OR DELETE ON invoices FOR EACH ROW EXECUTE FUNCTION tally_immutable();
        DROP TRIGGER IF EXISTS invoice_lines_immutable ON invoice_lines;
        CREATE TRIGGER invoice_lines_immutable BEFORE UPDATE OR DELETE ON invoice_lines FOR EACH ROW EXECUTE FUNCTION tally_immutable();
        """;

    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(727002)", connection, tx))
        {
            await advisory.ExecuteNonQueryAsync(ct);
        }

        await using (var create = new NpgsqlCommand(Schema, connection, tx))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<int> PublishPlanAsync(Plan plan, CancellationToken ct = default)
    {
        var errors = plan.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors));
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext(@id))", connection, tx))
        {
            advisory.Parameters.AddWithValue("id", plan.Id);
            await advisory.ExecuteNonQueryAsync(ct);
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO plans (id, version, body) SELECT @id, COALESCE(MAX(version), 0) + 1, @body FROM plans WHERE id = @id RETURNING version",
            connection, tx);
        insert.Parameters.AddWithValue("id", plan.Id);
        insert.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(plan, PlanJson) });
        var version = (int)(await insert.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return version;
    }

    public async Task<bool> CreateCustomerAsync(string id, string planId, int? planVersion, DateOnly start, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
        {
            throw new ArgumentException("customer id must be 1..128 characters");
        }

        await using var cmd = dataSource.CreateCommand(
            """
            INSERT INTO customers (id, plan_id, plan_version, start_date)
            SELECT @id, @plan, COALESCE(@version, (SELECT MAX(version) FROM plans WHERE id = @plan)), @start
            WHERE EXISTS (SELECT 1 FROM plans WHERE id = @plan AND version = COALESCE(@version, (SELECT MAX(version) FROM plans WHERE id = @plan)))
            ON CONFLICT (id) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("plan", planId);
        cmd.Parameters.Add(new NpgsqlParameter("version", NpgsqlDbType.Integer) { Value = (object?)planVersion ?? DBNull.Value });
        cmd.Parameters.AddWithValue("start", start);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 1)
        {
            return true;
        }

        await using var exists = dataSource.CreateCommand("SELECT 1 FROM customers WHERE id = @id");
        exists.Parameters.AddWithValue("id", id);
        if (await exists.ExecuteScalarAsync(ct) is not null)
        {
            return false;
        }

        throw new ArgumentException($"plan '{planId}'{(planVersion is null ? "" : $" version {planVersion}")} does not exist");
    }

    public async Task<IngestResult> IngestAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
        {
            return new IngestResult(0, 0, 0, 0);
        }

        if (events.Count > MaxBatch)
        {
            throw new ArgumentException($"a batch may contain at most {MaxBatch} events");
        }

        foreach (var e in events)
        {
            if (string.IsNullOrWhiteSpace(e.CustomerId) || e.CustomerId.Length > 128)
            {
                throw new ArgumentException("customerId must be 1..128 characters");
            }

            if (string.IsNullOrWhiteSpace(e.EventId) || e.EventId.Length > 128)
            {
                throw new ArgumentException("eventId must be 1..128 characters");
            }

            if (!MeterName().IsMatch(e.Meter))
            {
                throw new ArgumentException($"meter '{e.Meter}' is not a valid meter name");
            }

            if (e.Quantity <= 0 || e.Quantity > MaxQuantity)
            {
                throw new ArgumentException($"quantity must be greater than 0 and at most {MaxQuantity}");
            }

            if (e.EventTime.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("eventTime must be UTC");
            }
        }

        await using var cmd = dataSource.CreateCommand(
            """
            WITH input AS (
                SELECT * FROM unnest(@customer::text[], @event::text[], @meter::text[], @qty::numeric[], @time::timestamptz[])
                    AS t (customer_id, event_id, meter, quantity, event_time)),
            valid AS (
                SELECT i.* FROM input i
                JOIN customers c ON c.id = i.customer_id
                WHERE i.event_time >= (c.start_date::timestamp AT TIME ZONE 'UTC')),
            ins AS (
                INSERT INTO usage_events (customer_id, event_id, meter, quantity, event_time, period)
                SELECT customer_id, event_id, meter, quantity, event_time, to_char(event_time AT TIME ZONE 'UTC', 'YYYY-MM')
                FROM valid
                ORDER BY customer_id, event_id
                ON CONFLICT DO NOTHING
                RETURNING customer_id, meter, period, quantity),
            agg AS (
                SELECT customer_id, meter, period, sum(quantity) AS q, count(*) AS n
                FROM ins GROUP BY customer_id, meter, period
                ORDER BY customer_id, meter, period),
            up AS (
                INSERT INTO usage_rollups (customer_id, meter, period, quantity, events)
                SELECT customer_id, meter, period, q, n FROM agg
                ON CONFLICT (customer_id, meter, period) DO UPDATE
                SET quantity = usage_rollups.quantity + EXCLUDED.quantity, events = usage_rollups.events + EXCLUDED.events
                RETURNING 1)
            SELECT (SELECT count(*) FROM input), (SELECT count(*) FROM valid), (SELECT count(*) FROM ins), (SELECT count(*) FROM up)
            """);
        cmd.Parameters.Add(new NpgsqlParameter("customer", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = events.Select(e => e.CustomerId).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("event", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = events.Select(e => e.EventId).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("meter", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = events.Select(e => e.Meter).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("qty", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = events.Select(e => e.Quantity).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("time", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = events.Select(e => e.EventTime).ToArray() });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var received = (int)reader.GetInt64(0);
        var valid = (int)reader.GetInt64(1);
        var inserted = (int)reader.GetInt64(2);
        return new IngestResult(received, inserted, valid - inserted, received - valid);
    }

    public async Task<IReadOnlyList<UsageSummary>?> UsageAsync(string customerId, string? period, CancellationToken ct = default)
    {
        var plan = await LoadPlanAsync(customerId, ct);
        if (plan is null)
        {
            return null;
        }

        await using var cmd = dataSource.CreateCommand(
            "SELECT meter, period, quantity, events, billed_amount FROM usage_rollups WHERE customer_id = @id AND (@period::text IS NULL OR period = @period) ORDER BY period, meter");
        cmd.Parameters.AddWithValue("id", customerId);
        cmd.Parameters.Add(new NpgsqlParameter("period", NpgsqlDbType.Text) { Value = (object?)period ?? DBNull.Value });
        var list = new List<UsageSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var meter = reader.GetString(0);
            var quantity = reader.GetDecimal(2);
            var pricing = plan.Plan.Find(meter);
            var cost = pricing is null ? 0m : PricingCalculator.Cost(plan.Plan, pricing, quantity);
            list.Add(new UsageSummary(meter, reader.GetString(1), quantity, reader.GetInt64(3), cost, reader.GetDecimal(4)));
        }

        return list;
    }

    public async Task<BillingOutcome?> BillAsync(string customerId, DateTime asOf, bool commit, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using var customerCmd = new NpgsqlCommand(
            "SELECT c.start_date, c.next_invoice_no, p.body::text FROM customers c JOIN plans p ON p.id = c.plan_id AND p.version = c.plan_version WHERE c.id = @id FOR UPDATE OF c",
            connection, tx);
        customerCmd.Parameters.AddWithValue("id", customerId);
        DateOnly start;
        int number;
        Plan plan;
        await using (var reader = await customerCmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            start = DateOnly.FromDateTime(reader.GetDateTime(0));
            number = reader.GetInt32(1);
            plan = JsonSerializer.Deserialize<Plan>(reader.GetString(2), PlanJson)!;
        }

        var lastDue = PricingCalculator.PeriodOf(new DateTime(asOf.Year, asOf.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1));
        var startPeriod = PricingCalculator.PeriodOf(start.ToDateTime(TimeOnly.MinValue));
        var lines = new List<InvoiceLine>();
        var unpriced = new SortedSet<string>(StringComparer.Ordinal);
        var rollupUpdates = new List<(string Meter, string Period, decimal Quantity, decimal Amount)>();
        var baseFeePeriods = new List<string>();

        var billedBase = new HashSet<string>();
        await using (var billed = new NpgsqlCommand("SELECT period FROM base_fees_billed WHERE customer_id = @id", connection, tx))
        {
            billed.Parameters.AddWithValue("id", customerId);
            await using var r = await billed.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                billedBase.Add(r.GetString(0));
            }
        }

        var lineNo = 1;
        var usageLines = new List<InvoiceLine>();
        if (plan.BaseFee > 0)
        {
            for (var p = startPeriod; string.CompareOrdinal(p, lastDue) <= 0; p = Next(p))
            {
                if (billedBase.Contains(p))
                {
                    continue;
                }

                var fee = PricingCalculator.ProratedBaseFee(plan, start, p);
                baseFeePeriods.Add(p);
                lines.Add(new InvoiceLine(lineNo++, "base", p, null, $"Base fee {p}", 1, fee));
            }
        }

        await using (var rollups = new NpgsqlCommand(
            $"SELECT meter, period, quantity, billed_quantity, billed_amount FROM usage_rollups WHERE customer_id = @id AND period <= @last AND period >= @first AND quantity <> billed_quantity ORDER BY meter, period {(commit ? "FOR UPDATE" : "")}",
            connection, tx))
        {
            rollups.Parameters.AddWithValue("id", customerId);
            rollups.Parameters.AddWithValue("last", lastDue);
            rollups.Parameters.AddWithValue("first", startPeriod);
            await using var r = await rollups.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var meter = r.GetString(0);
                var period = r.GetString(1);
                var quantity = r.GetDecimal(2);
                var billedQuantity = r.GetDecimal(3);
                var billedAmount = r.GetDecimal(4);
                var pricing = plan.Find(meter);
                if (pricing is null)
                {
                    unpriced.Add(meter);
                    continue;
                }

                var desired = PricingCalculator.Cost(plan, pricing, quantity);
                var kind = billedQuantity == 0 ? "usage" : "adjustment";
                var description = billedQuantity == 0
                    ? FormattableString.Invariant($"{meter} {period}: {quantity:0.######} units")
                    : FormattableString.Invariant($"{meter} {period}: late usage, {quantity - billedQuantity:0.######} units (total now {quantity:0.######})");
                usageLines.Add(new InvoiceLine(0, kind, period, meter, description, quantity - billedQuantity, desired - billedAmount));
                rollupUpdates.Add((meter, period, quantity, desired));
            }
        }

        foreach (var l in usageLines.OrderBy(l => l.Period, StringComparer.Ordinal).ThenBy(l => l.Meter, StringComparer.Ordinal))
        {
            lines.Add(l with { LineNo = lineNo++ });
        }

        if (lines.Count == 0)
        {
            return new BillingOutcome(null, unpriced.ToList());
        }

        var total = lines.Sum(l => l.Amount);
        if (!commit)
        {
            return new BillingOutcome(new Invoice(0, customerId, number, plan.Currency, total, DateTime.UtcNow, lines), unpriced.ToList());
        }

        long invoiceId;
        DateTime createdAt;
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO invoices (customer_id, number, currency, total) VALUES (@id, @number, @currency, @total) RETURNING id, created_at", connection, tx))
        {
            insert.Parameters.AddWithValue("id", customerId);
            insert.Parameters.AddWithValue("number", number);
            insert.Parameters.AddWithValue("currency", plan.Currency);
            insert.Parameters.AddWithValue("total", total);
            await using var r = await insert.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            invoiceId = r.GetInt64(0);
            createdAt = r.GetDateTime(1);
        }

        await using (var batch = new NpgsqlBatch(connection, tx))
        {
            foreach (var l in lines)
            {
                var cmd = new NpgsqlBatchCommand(
                    "INSERT INTO invoice_lines (invoice_id, line_no, kind, period, meter, description, quantity, amount) VALUES (@inv, @no, @kind, @period, @meter, @desc, @qty, @amount)");
                cmd.Parameters.AddWithValue("inv", invoiceId);
                cmd.Parameters.AddWithValue("no", l.LineNo);
                cmd.Parameters.AddWithValue("kind", l.Kind);
                cmd.Parameters.AddWithValue("period", l.Period);
                cmd.Parameters.Add(new NpgsqlParameter("meter", NpgsqlDbType.Text) { Value = (object?)l.Meter ?? DBNull.Value });
                cmd.Parameters.AddWithValue("desc", l.Description);
                cmd.Parameters.AddWithValue("qty", l.Quantity);
                cmd.Parameters.AddWithValue("amount", l.Amount);
                batch.BatchCommands.Add(cmd);
            }

            foreach (var u in rollupUpdates)
            {
                var cmd = new NpgsqlBatchCommand(
                    "UPDATE usage_rollups SET billed_quantity = @q, billed_amount = @a WHERE customer_id = @id AND meter = @meter AND period = @period");
                cmd.Parameters.AddWithValue("q", u.Quantity);
                cmd.Parameters.AddWithValue("a", u.Amount);
                cmd.Parameters.AddWithValue("id", customerId);
                cmd.Parameters.AddWithValue("meter", u.Meter);
                cmd.Parameters.AddWithValue("period", u.Period);
                batch.BatchCommands.Add(cmd);
            }

            foreach (var p in baseFeePeriods)
            {
                var cmd = new NpgsqlBatchCommand("INSERT INTO base_fees_billed (customer_id, period, invoice_id) VALUES (@id, @period, @inv)");
                cmd.Parameters.AddWithValue("id", customerId);
                cmd.Parameters.AddWithValue("period", p);
                cmd.Parameters.AddWithValue("inv", invoiceId);
                batch.BatchCommands.Add(cmd);
            }

            var bump = new NpgsqlBatchCommand("UPDATE customers SET next_invoice_no = next_invoice_no + 1 WHERE id = @id");
            bump.Parameters.AddWithValue("id", customerId);
            batch.BatchCommands.Add(bump);
            await batch.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new BillingOutcome(new Invoice(invoiceId, customerId, number, plan.Currency, total, createdAt, lines), unpriced.ToList());
    }

    public async Task<IReadOnlyList<Invoice>> InvoicesAsync(string customerId, CancellationToken ct = default)
    {
        var invoices = new List<(long Id, int Number, string Currency, decimal Total, DateTime At)>();
        await using (var cmd = dataSource.CreateCommand(
            "SELECT id, number, currency, total, created_at FROM invoices WHERE customer_id = @id ORDER BY number"))
        {
            cmd.Parameters.AddWithValue("id", customerId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                invoices.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetDecimal(3), reader.GetDateTime(4)));
            }
        }

        var result = new List<Invoice>();
        foreach (var (id, number, currency, total, at) in invoices)
        {
            var lines = new List<InvoiceLine>();
            await using var cmd = dataSource.CreateCommand(
                "SELECT line_no, kind, period, meter, description, quantity, amount FROM invoice_lines WHERE invoice_id = @id ORDER BY line_no");
            cmd.Parameters.AddWithValue("id", id);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                lines.Add(new InvoiceLine(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4), reader.GetDecimal(5), reader.GetDecimal(6)));
            }

            result.Add(new Invoice(id, customerId, number, currency, total, at, lines));
        }

        return result;
    }

    private sealed record LoadedPlan(Plan Plan, DateOnly Start);

    private async Task<LoadedPlan?> LoadPlanAsync(string customerId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT p.body::text, c.start_date FROM customers c JOIN plans p ON p.id = c.plan_id AND p.version = c.plan_version WHERE c.id = @id");
        cmd.Parameters.AddWithValue("id", customerId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new LoadedPlan(JsonSerializer.Deserialize<Plan>(reader.GetString(0), PlanJson)!, DateOnly.FromDateTime(reader.GetDateTime(1)))
            : null;
    }

    private static string Next(string period)
    {
        var (_, next) = PricingCalculator.PeriodBounds(period);
        return $"{next.Year:D4}-{next.Month:D2}";
    }
}
