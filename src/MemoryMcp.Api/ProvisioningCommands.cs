using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Api;

/// <summary>
/// One-shot provisioning verbs — <c>--create-user</c>, <c>--create-api-key</c> and
/// <c>--create-space</c> — run instead of the HTTP host, in the same spirit as <c>--seed</c> and
/// <c>--migrate</c>.
/// </summary>
/// <remarks>
/// <para>These exist because <c>--seed</c> is a dev fixture, not provisioning (TODO T9): before this,
/// onboarding a real person meant writing <c>users</c>, <c>api_keys</c> and <c>api_key_space_grants</c>
/// rows by hand, and computing the key hash outside the code that verifies it. Deliberately separate
/// verbs rather than one: a person outlives any single credential they hold, so minting a second key
/// for an existing user must not be a special case of creating them — and a space outlives the keys
/// granted on it, so opening a space to one more credential must not mean rotating that credential.</para>
/// <para>They are hosted here, on the <see cref="WebApplication"/> path, rather than in a builder of
/// their own, so they read exactly the configuration the server reads — same
/// <c>ConnectionStrings:Default</c>, same <c>appsettings.{Environment}.json</c>. Migrations are
/// <em>not</em> applied: this writes data into a schema that is expected to already exist.</para>
/// </remarks>
internal static class ProvisioningCommands
{
    private const string CreateUserVerb = "--create-user";
    private const string CreateApiKeyVerb = "--create-api-key";
    private const string CreateSpaceVerb = "--create-space";

    /// <summary>The verb present in <paramref name="args"/>, or <c>null</c> if this is a normal run.</summary>
    public static string? FindVerb(string[] args) =>
        args.FirstOrDefault(a => a is CreateUserVerb or CreateApiKeyVerb or CreateSpaceVerb);

    /// <summary>Runs <paramref name="verb"/> and returns the process exit code.</summary>
    public static async Task<int> RunAsync(string verb, string[] args, IServiceProvider services)
    {
        Options options;
        try
        {
            options = Options.Parse(args, verb);
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        try
        {
            return verb switch
            {
                CreateUserVerb => await CreateUserAsync(db, options),
                CreateApiKeyVerb => await CreateApiKeyAsync(db, options),
                CreateSpaceVerb => await CreateSpaceAsync(db, options),
                _ => Fail($"Unknown provisioning verb '{verb}'."),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static async Task<int> CreateUserAsync(MemoryDbContext db, Options options)
    {
        var email = User.NormalizeEmail(options.Required("email"));
        var displayName = options.Optional("name") ?? email;
        var role = options.Enum("role", UserRole.Writer);

        var existing = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);
        if (existing is not null)
        {
            return Fail(
                $"A user with email '{email}' already exists (id {existing.Id}, {existing.Role}, " +
                $"{(existing.IsActive ? "active" : "deactivated")}). Mint another credential for them with " +
                $"{CreateApiKeyVerb} instead of creating a second account.");
        }

        var user = new User(email, displayName, role);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Console.WriteLine($"Created user {user.DisplayName} <{user.Email}>");
        Console.WriteLine($"  id:   {user.Id}");
        Console.WriteLine($"  role: {user.Role} (may {(user.Role == UserRole.Writer ? "read and write" : "only read")} in every space granted to them)");
        Console.WriteLine();
        Console.WriteLine($"They cannot authenticate yet — no credential exists. Next:");
        Console.WriteLine($"  dotnet run --project src/MemoryMcp.Api -- {CreateApiKeyVerb} --email {user.Email} --space <space-key>");
        return 0;
    }

    private static async Task<int> CreateApiKeyAsync(MemoryDbContext db, Options options)
    {
        var email = User.NormalizeEmail(options.Required("email"));
        var label = options.Optional("label");
        var requestedGrants = ParseGrants(options.All("space"));
        var defaultSpaceKey = options.Optional("default-space");

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);
        if (user is null)
        {
            return Fail($"No user with email '{email}'. Create them first with {CreateUserVerb} --email {email}.");
        }

        var spaces = await db.Spaces.AsNoTracking()
            .Where(s => requestedGrants.Keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s);

        var missing = requestedGrants.Keys.Where(k => !spaces.ContainsKey(k)).ToList();
        if (missing.Count > 0)
        {
            var available = await db.Spaces.AsNoTracking().OrderBy(s => s.Key).Select(s => s.Key).ToListAsync();
            return Fail(
                $"Unknown space(s): {string.Join(", ", missing)}. Existing spaces: " +
                (available.Count > 0 ? string.Join(", ", available) : "(none)") + ".");
        }

        // Exactly one grant carries IsDefault, because it answers a single question ("which space when
        // the caller names none?") that two rows cannot both answer.
        if (defaultSpaceKey is not null && !requestedGrants.ContainsKey(defaultSpaceKey))
        {
            return Fail($"--default-space '{defaultSpaceKey}' is not among the granted spaces ({string.Join(", ", requestedGrants.Keys)}).");
        }

        defaultSpaceKey ??= requestedGrants.Keys.FirstOrDefault();

        var rawKey = $"mmcp_{Guid.NewGuid():N}";
        var apiKey = new ApiKey(user.Id, ApiKeyHasher.Hash(rawKey), rawKey[..12], label);
        db.ApiKeys.Add(apiKey);

        foreach (var (spaceKey, level) in requestedGrants)
        {
            db.ApiKeySpaceGrants.Add(new ApiKeySpaceGrant(apiKey.Id, spaces[spaceKey].Id, level, spaceKey == defaultSpaceKey));
        }

        await db.SaveChangesAsync();

        Console.WriteLine($"Created API key for {user.DisplayName} <{user.Email}> ({user.Role})");
        Console.WriteLine($"  key id: {apiKey.Id}{(label is null ? "" : $"  label: {label}")}");
        Console.WriteLine();
        Console.WriteLine($"  API key: {rawKey}");
        Console.WriteLine("  ^ only the SHA-256 hash is stored, so this is the one and only time it is shown.");
        Console.WriteLine("    Send it as the X-Api-Key header (or MEMORYMCP_API_KEY for --stdio).");
        Console.WriteLine();

        if (requestedGrants.Count == 0)
        {
            Console.WriteLine("  Spaces: none — this key authenticates but can reach nothing. Re-run with --space <key>.");
        }
        else
        {
            // The role caps the grant, so a Reader handed --space team:ReadWrite is still read-only there.
            // Printing both levels makes that visible now rather than as a puzzling denial later.
            var ceiling = user.Role.MaxAccessLevel();
            Console.WriteLine("  Spaces:");
            foreach (var (spaceKey, level) in requestedGrants)
            {
                var effective = level < ceiling ? level : ceiling;
                var capped = effective != level ? $" (grant {level}, capped by {user.Role} role)" : "";
                var isDefault = spaceKey == defaultSpaceKey ? " [default]" : "";
                Console.WriteLine($"    {spaceKey}: {effective}{capped}{isDefault}");
            }
        }

        if (!user.IsActive)
        {
            Console.WriteLine();
            Console.WriteLine($"  WARNING: {user.Email} is deactivated, so this key will be rejected until the account is reactivated.");
        }

        return 0;
    }

    private static async Task<int> CreateSpaceAsync(MemoryDbContext db, Options options)
    {
        var key = options.Required("key").Trim();
        if (key.Length == 0)
        {
            return Fail("--key must not be empty.");
        }

        var name = options.Optional("name") ?? key;
        var description = options.Optional("description");
        var allowExisting = options.Flag("allow-existing");
        var makeDefault = options.Flag("make-default");
        var requestedGrants = ParseGrants(options.All("grant"), "grant");

        var space = await db.Spaces.FirstOrDefaultAsync(s => s.Key == key);
        var created = space is null;

        if (space is not null && !allowExisting)
        {
            return Fail(
                $"A space with key '{key}' already exists (id {space.Id}, name '{space.Name}'). Pass " +
                "--allow-existing to grant that space to more keys instead of creating it.");
        }

        if (space is null)
        {
            space = new Space(key, name, description);
            db.Spaces.Add(space);
        }

        // Resolved before anything is saved, so a typo in one target doesn't leave a space created and
        // half the grants applied.
        var targets = new Dictionary<Guid, (ApiKey Key, User Owner, AccessLevel Level, string Spec)>();
        foreach (var (spec, level) in requestedGrants)
        {
            var matches = await ResolveGrantTargetsAsync(db, spec);
            if (matches.Count == 0)
            {
                return Fail(await DescribeUnresolvedTargetAsync(db, spec, key));
            }

            foreach (var (apiKey, owner) in matches)
            {
                if (targets.TryGetValue(apiKey.Id, out var already) && already.Level != level)
                {
                    return Fail(
                        $"--grant '{spec}' and '{already.Spec}' both resolve to key {apiKey.KeyPrefix}… " +
                        $"({owner.Email}) but ask for different levels ({level} vs {already.Level}).");
                }

                targets[apiKey.Id] = (apiKey, owner, level, spec);
            }
        }

        // Tracked, because applying a default flips IsDefault on whichever grant currently holds it.
        // A grant on *this* space is only found with --allow-existing: a new space has none yet.
        var targetIds = targets.Keys.ToList();
        List<ApiKeySpaceGrant> existingGrants = targetIds.Count == 0
            ? []
            : await db.ApiKeySpaceGrants.Where(g => targetIds.Contains(g.ApiKeyId)).ToListAsync();

        var applied = new List<(ApiKey Key, User Owner, AccessLevel Level, bool IsDefault, bool AlreadyGranted)>();

        foreach (var (apiKey, owner, level, _) in targets.Values)
        {
            var keyGrants = existingGrants.Where(g => g.ApiKeyId == apiKey.Id).ToList();
            var alreadyGranted = keyGrants.FirstOrDefault(g => g.SpaceId == space.Id);

            // A key with no default has no active space, so the first space granted to it becomes the
            // one it falls back on — the same rule --create-api-key applies to its first --space.
            var isDefault = makeDefault || keyGrants.Count == 0 || !keyGrants.Any(g => g.IsDefault);

            if (isDefault)
            {
                // ActiveGrant is `Grants.FirstOrDefault(g => g.IsDefault)`, so two defaults on one key
                // would make the fallback space depend on row order. Clear the old one instead.
                foreach (var other in keyGrants.Where(g => g.IsDefault && g.SpaceId != space.Id))
                {
                    other.SetAsDefault(false);
                }
            }

            if (alreadyGranted is not null)
            {
                alreadyGranted.SetAsDefault(isDefault);
                applied.Add((apiKey, owner, alreadyGranted.AccessLevel, isDefault, true));
                continue;
            }

            db.ApiKeySpaceGrants.Add(new ApiKeySpaceGrant(apiKey.Id, space.Id, level, isDefault));
            applied.Add((apiKey, owner, level, isDefault, false));
        }

        await db.SaveChangesAsync();

        Console.WriteLine($"{(created ? "Created" : "Reusing")} space '{space.Key}' — {space.Name}");
        Console.WriteLine($"  id: {space.Id}");
        if (space.Description is not null)
        {
            Console.WriteLine($"  description: {space.Description}");
        }

        Console.WriteLine();

        if (applied.Count == 0)
        {
            Console.WriteLine("  Grants: none — no key can reach this space yet. Either:");
            Console.WriteLine($"    re-run with {CreateSpaceVerb} --key {space.Key} --allow-existing --grant <email>");
            Console.WriteLine($"    or mint a key on it: {CreateApiKeyVerb} --email <email> --space {space.Key}");
            return 0;
        }

        Console.WriteLine("  Grants:");
        foreach (var (apiKey, owner, level, isDefault, alreadyGranted) in applied.OrderBy(a => a.Owner.Email))
        {
            // The role caps the grant, so a Reader granted ReadWrite is still read-only here. Printing
            // both makes that visible now rather than as a puzzling denial later.
            var ceiling = owner.Role.MaxAccessLevel();
            var effective = level < ceiling ? level : ceiling;
            var capped = effective != level ? $" (grant {level}, capped by {owner.Role} role)" : "";
            var label = apiKey.Label is null ? "" : $", {apiKey.Label}";
            var flags = string.Concat(
                isDefault ? " [default]" : "",
                alreadyGranted ? " (already granted, level left unchanged)" : "");

            Console.WriteLine($"    {owner.DisplayName} <{owner.Email}> ({apiKey.KeyPrefix}…{label}): {effective}{capped}{flags}");

            if (!apiKey.IsActive || !owner.IsActive)
            {
                var what = !owner.IsActive ? $"{owner.Email} is deactivated" : "this key is revoked";
                Console.WriteLine($"      WARNING: {what}, so the grant has no effect until that is undone.");
            }
        }

        return 0;
    }

    /// <summary>
    /// Resolves one <c>--grant</c> target to the keys it names: an email means every key its owner
    /// holds, anything else is a single credential by id or by printed prefix.
    /// </summary>
    /// <remarks>
    /// An email fans out to all of the person's keys on purpose — access is granted to a person's
    /// laptop *and* their CI runner, and a caller who wanted only one of them can name that key
    /// directly. Revoked keys and deactivated owners are still resolved rather than skipped, so
    /// granting to them reports the warning instead of silently doing nothing.
    /// </remarks>
    private static async Task<List<(ApiKey Key, User Owner)>> ResolveGrantTargetsAsync(MemoryDbContext db, string spec)
    {
        var pairs = db.ApiKeys.AsNoTracking()
            .Join(db.Users.AsNoTracking(), k => k.UserId, u => u.Id, (k, u) => new { Key = k, Owner = u });

        if (spec.Contains('@'))
        {
            var email = User.NormalizeEmail(spec);
            var owned = await pairs.Where(p => p.Owner.Email == email).ToListAsync();
            return owned.Select(p => (p.Key, p.Owner)).ToList();
        }

        if (Guid.TryParse(spec, out var keyId))
        {
            var byId = await pairs.Where(p => p.Key.Id == keyId).ToListAsync();
            return byId.Select(p => (p.Key, p.Owner)).ToList();
        }

        // KeyPrefix stores the first 12 characters of the raw key, so a caller can paste either the
        // prefix shown by listings or the whole key they were handed.
        var prefix = spec.Length > 12 ? spec[..12] : spec;
        var byPrefix = await pairs.Where(p => p.Key.KeyPrefix.StartsWith(prefix)).ToListAsync();
        return byPrefix.Select(p => (p.Key, p.Owner)).ToList();
    }

    /// <summary>
    /// Why a target matched nothing. An email that resolves to no key has two very different causes —
    /// nobody by that address, or a person who holds no credential yet — and telling someone to create
    /// a user that already exists sends them down the wrong path.
    /// </summary>
    private static async Task<string> DescribeUnresolvedTargetAsync(MemoryDbContext db, string spec, string spaceKey)
    {
        if (!spec.Contains('@'))
        {
            return $"No API key matches '{spec}'. Expected an owner's email, a key id (GUID), or the key prefix shown when it was minted.";
        }

        var email = User.NormalizeEmail(spec);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);

        return user is null
            ? $"No user with email '{email}'. Create them with {CreateUserVerb} --email {email}, then mint a key with {CreateApiKeyVerb}."
            : $"{user.DisplayName} <{email}> holds no API key, so there is nothing to grant. Mint one already " +
              $"granted on this space instead: {CreateApiKeyVerb} --email {email} --space {spaceKey}.";
    }

    /// <summary>Parses repeated <c>--<paramref name="optionName"/> name[:Read|ReadWrite]</c> options,
    /// defaulting to ReadWrite.</summary>
    private static Dictionary<string, AccessLevel> ParseGrants(IReadOnlyList<string> specs, string optionName = "space")
    {
        var grants = new Dictionary<string, AccessLevel>();
        foreach (var spec in specs)
        {
            var separator = spec.LastIndexOf(':');
            var key = separator < 0 ? spec : spec[..separator];
            var levelText = separator < 0 ? nameof(AccessLevel.ReadWrite) : spec[(separator + 1)..];

            if (key.Length == 0)
            {
                throw new ArgumentException($"--{optionName} '{spec}' names nothing. Expected '<name>[:Read|ReadWrite]'.");
            }

            if (!System.Enum.TryParse<AccessLevel>(levelText, ignoreCase: true, out var level))
            {
                throw new ArgumentException($"--{optionName} '{spec}' has an unknown access level '{levelText}'. Expected Read or ReadWrite.");
            }

            if (!grants.TryAdd(key, level))
            {
                throw new ArgumentException($"--{optionName} '{key}' was given more than once.");
            }
        }

        return grants;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    /// <summary>
    /// A minimal <c>--name value</c> parser, hand-rolled rather than delegating to
    /// <c>AddCommandLine</c>: the configuration provider throws on a valueless flag followed by another
    /// flag, which is exactly the shape of <c>--create-user --email …</c>.
    /// </summary>
    private sealed class Options
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Options Parse(string[] args, string verb)
        {
            var options = new Options();
            string? pending = null;

            foreach (var arg in args)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    pending = null;
                    var body = arg[2..];
                    var separator = body.IndexOf('=');
                    if (separator >= 0)
                    {
                        options.Add(body[..separator], body[(separator + 1)..]);
                    }
                    else if (arg != verb)
                    {
                        pending = body;
                        options._values.TryAdd(body, []);
                    }
                }
                else if (pending is not null)
                {
                    options.Add(pending, arg);
                    pending = null;
                }
                else
                {
                    throw new ArgumentException($"Unexpected argument '{arg}'. Every value must follow a --option.");
                }
            }

            return options;
        }

        private void Add(string name, string value)
        {
            if (!_values.TryGetValue(name, out var existing))
            {
                _values[name] = existing = [];
            }

            existing.Add(value);
        }

        public string Required(string name) =>
            Optional(name) ?? throw new ArgumentException($"--{name} is required.");

        public string? Optional(string name)
        {
            var all = All(name);
            return all.Count switch
            {
                0 => null,
                1 => all[0],
                _ => throw new ArgumentException($"--{name} was given more than once."),
            };
        }

        public IReadOnlyList<string> All(string name) =>
            _values.TryGetValue(name, out var values) ? values : [];

        /// <summary>
        /// Whether a valueless switch was given. Presence alone is the answer: <see cref="Parse"/>
        /// registers <c>--flag</c> with an empty value list, so a switch is indistinguishable from an
        /// option whose value is still to come — and no switch here takes one.
        /// </summary>
        public bool Flag(string name) => _values.ContainsKey(name);

        public TEnum Enum<TEnum>(string name, TEnum fallback) where TEnum : struct, System.Enum
        {
            var raw = Optional(name);
            if (raw is null)
            {
                return fallback;
            }

            return System.Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed)
                ? parsed
                : throw new ArgumentException($"--{name} '{raw}' is not valid. Expected one of: {string.Join(", ", System.Enum.GetNames<TEnum>())}.");
        }
    }
}
