using ProtectMe.Core;

namespace ProtectMe.Service;

public sealed class ProtectionStateManager
{
    private readonly object _lock = new();
    private ProtectConfig _config;

    public ProtectionStateManager()
    {
        _config = ConfigStore.Load();
    }

    public ProtectConfig GetSnapshot()
    {
        lock (_lock)
        {
            return ConfigStore.Load();
        }
    }

    public ProtectConfig Current
    {
        get
        {
            lock (_lock)
            {
                return _config;
            }
        }
    }

    public void Update(Action<ProtectConfig> mutator)
    {
        lock (_lock)
        {
            mutator(_config);
            ConfigStore.Save(_config);
        }
    }

    public void RecordBlockedEvent(string kind, string target, string domain)
    {
        lock (_lock)
        {
            var recent = _config.RecentBlocked;
            // Avoid spamming duplicate entries within 5 seconds
            var last = recent.FirstOrDefault();
            if (last != null &&
                last.Target.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                (DateTime.UtcNow - last.TimestampUtc).TotalSeconds < 5)
            {
                return;
            }

            recent.Insert(0, new BlockedEvent
            {
                TimestampUtc = DateTime.UtcNow,
                Kind = kind,
                Target = target,
                Domain = domain
            });

            if (recent.Count > 40)
            {
                recent.RemoveRange(40, recent.Count - 40);
            }

            ConfigStore.Save(_config);
        }
    }

    public bool VerifyPassword(string? password)
    {
        lock (_lock)
        {
            if (!_config.HasPassword) return true;
            return SecurityHelper.VerifyPassword(password ?? string.Empty, _config.PasswordHash, _config.PasswordSalt);
        }
    }
}
