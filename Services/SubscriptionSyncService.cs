using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class LoginSubscriptionLoadItem
{
    public required ServerSubscription Subscription { get; init; }
    public SubscriptionImportResult? ImportResult { get; init; }
    public string? ErrorMessage { get; init; }

    public bool Success => ImportResult is not null;
}

public sealed class ServerSubscriptionFetchResult
{
    public IReadOnlyList<ServerSubscription> Subscriptions { get; init; } = [];
    public bool RetrievedFromServer { get; init; }
    public bool IsTransientFailure { get; init; }
    public string? ErrorMessage { get; init; }

    public static ServerSubscriptionFetchResult FromServer(IReadOnlyList<ServerSubscription> subscriptions) =>
        new() { Subscriptions = subscriptions, RetrievedFromServer = true };

    public static ServerSubscriptionFetchResult CachedFailure(
        IReadOnlyList<ServerSubscription> subscriptions,
        bool isTransient,
        string? message) =>
        new()
        {
            Subscriptions = subscriptions,
            RetrievedFromServer = false,
            IsTransientFailure = isTransient,
            ErrorMessage = message
        };
}

public sealed class SubscriptionSyncService
{
    private readonly AuthService _authService;
    private readonly SubscriptionApiService _subscriptionApi;
    private readonly SubscriptionSnapshotStore _snapshotStore = new();

    public SubscriptionSyncService(AuthService authService, SubscriptionApiService subscriptionApi)
    {
        _authService = authService;
        _subscriptionApi = subscriptionApi;
    }

    public async Task<IReadOnlyList<LoginSubscriptionLoadItem>> LoadLoginSubscriptionsAsync(
        IEnumerable<ServerSubscription> subscriptions,
        CancellationToken cancellationToken = default)
    {
        var tasks = subscriptions.Select(async subscription =>
        {
            if (string.IsNullOrWhiteSpace(subscription.Url))
            {
                return new LoginSubscriptionLoadItem
                {
                    Subscription = subscription,
                    ErrorMessage = "订阅链接为空，已跳过。"
                };
            }

            try
            {
                var importResult = await SubscriptionImportService.ImportAsync(subscription.Url, cancellationToken);
                var normalized = NormalizeImportResult(importResult, subscription);
                DiagnosticLogService.Info(
                    $"Loaded subscription \"{subscription.Name}\" with {normalized.Profiles.Count} node(s).");

                return new LoginSubscriptionLoadItem
                {
                    Subscription = subscription,
                    ImportResult = normalized
                };
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"Failed to load subscription \"{subscription.Name}\": {ex.Message}");
                return new LoginSubscriptionLoadItem
                {
                    Subscription = subscription,
                    ErrorMessage = ex.Message
                };
            }
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<ServerSubscriptionFetchResult> FetchLoginSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _authService.TryRestoreSessionAsync(cancellationToken))
        {
            return ServerSubscriptionFetchResult.CachedFailure([], isTransient: false, "登录已过期，请重新登录。");
        }

        var listResult = await _subscriptionApi.ListAsync(cancellationToken);
        if (listResult.IsSuccess && listResult.Data is not null)
        {
            _authService.ReplaceSessionSubscriptions(listResult.Data);
            return ServerSubscriptionFetchResult.FromServer(listResult.Data);
        }

        var isTransient = listResult.Code == 408 ||
                          SubscriptionTrafficHelper.IsTransientNetworkError(listResult.Message);
        DiagnosticLogService.Warning(
            $"Failed to fetch subscription list from server: {listResult.Message}. Using cached session list.");

        return ServerSubscriptionFetchResult.CachedFailure(
            _authService.CurrentSession?.Subscriptions ?? [],
            isTransient,
            listResult.Message);
    }

    public async Task<IReadOnlyList<ServerSubscription>> GetLoginSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var fetchResult = await FetchLoginSubscriptionsAsync(cancellationToken);
        return fetchResult.Subscriptions;
    }

    public async Task<IReadOnlyList<LoginSubscriptionLoadItem>> LoadLoginSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var subscriptions = await GetLoginSubscriptionsAsync(cancellationToken);
        return await LoadLoginSubscriptionsAsync(subscriptions, cancellationToken);
    }

    internal static SubscriptionImportResult NormalizeImportResult(
        SubscriptionImportResult importResult,
        ServerSubscription subscription)
    {
        var updatedAt = DateTime.Now;
        foreach (var profile in importResult.Profiles)
        {
            profile.SubscriptionName = subscription.Name;
            profile.SubscriptionUpdatedAt = updatedAt;
            profile.UpdatedAt = updatedAt;
        }

        return new SubscriptionImportResult
        {
            Profiles = importResult.Profiles,
            TrafficInfo = importResult.TrafficInfo,
            SourceUrl = subscription.Url,
            SubscriptionName = subscription.Name
        };
    }

    public bool ResolveAndAssignServerSubscriptionId(SubscriptionSource source, string subscriptionName)
    {
        if (source.ServerSubscriptionId is int existingId && existingId > 0)
        {
            return true;
        }

        var resolvedId = ResolveServerSubscriptionId(source, subscriptionName);
        if (resolvedId is not int id || id <= 0)
        {
            return false;
        }

        source.ServerSubscriptionId = id;
        return true;
    }

    public int? ResolveServerSubscriptionId(SubscriptionSource source, string subscriptionName)
    {
        if (source.ServerSubscriptionId is int existingId && existingId > 0)
        {
            return existingId;
        }

        var sessionSubscriptions = _authService.CurrentSession?.Subscriptions ?? [];
        if (!string.IsNullOrWhiteSpace(source.Url))
        {
            var byUrl = sessionSubscriptions.FirstOrDefault(item =>
                string.Equals(item.Url, source.Url, StringComparison.OrdinalIgnoreCase));
            if (byUrl is not null)
            {
                return byUrl.Id;
            }
        }

        if (!string.IsNullOrWhiteSpace(subscriptionName))
        {
            var byName = sessionSubscriptions.FirstOrDefault(item =>
                string.Equals(item.Name, subscriptionName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName.Id;
            }

            var byDisplayName = sessionSubscriptions.FirstOrDefault(item =>
                string.Equals(item.Name, source.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (byDisplayName is not null)
            {
                return byDisplayName.Id;
            }
        }

        return null;
    }

    public async Task<SubscriptionSyncResult> SyncRefreshAsync(
        SubscriptionImportResult importResult,
        SubscriptionSource source,
        string subscriptionName,
        CancellationToken cancellationToken = default)
    {
        importResult = new SubscriptionImportResult
        {
            Profiles = importResult.Profiles,
            TrafficInfo = importResult.TrafficInfo,
            SourceUrl = string.IsNullOrWhiteSpace(importResult.SourceUrl) ? source.Url : importResult.SourceUrl,
            SubscriptionName = subscriptionName
        };

        if (!ResolveAndAssignServerSubscriptionId(source, subscriptionName))
        {
            return SubscriptionSyncResult.Fail("未找到服务端订阅 ID，请重新登录后再刷新订阅。");
        }

        var userId = _authService.CurrentSession?.UserId ?? 0;
        var serverId = source.ServerSubscriptionId!.Value;
        var snapshot = SubscriptionSnapshotBuilder.Build(
            importResult,
            serverId,
            userId,
            subscriptionName,
            source.Url,
            source.CreatedAtUtc);
        var snapshotPath = _snapshotStore.Save(snapshot, subscriptionName);

        if (!_authService.IsAuthenticated)
        {
            return SubscriptionSyncResult.LocalOnly(snapshotPath, snapshot);
        }

        var updateResult = await UpdateSubscriptionOnServerAsync(
            importResult,
            source,
            subscriptionName,
            serverId,
            cancellationToken);

        if (!updateResult.IsSuccess)
        {
            return SubscriptionSyncResult.Fail(updateResult.Message, serverId) with
            {
                SnapshotPath = snapshotPath,
                Snapshot = snapshot
            };
        }

        SaveSubscriptionToSession(serverId, subscriptionName, source.Url);
        return SubscriptionSyncResult.Ok(serverId, updateResult.Message) with
        {
            SnapshotPath = snapshotPath,
            Snapshot = snapshot
        };
    }

    public async Task<SubscriptionSyncResult> SyncImportAsync(
        SubscriptionImportResult importResult,
        SubscriptionSource source,
        string subscriptionName,
        CancellationToken cancellationToken = default)
    {
        var userId = _authService.CurrentSession?.UserId ?? 0;
        var serverId = source.ServerSubscriptionId ?? 0;
        var snapshot = SubscriptionSnapshotBuilder.Build(
            importResult,
            serverId > 0 ? serverId : 1,
            userId,
            subscriptionName,
            source.Url,
            source.CreatedAtUtc);

        var snapshotPath = _snapshotStore.Save(snapshot, subscriptionName);
        if (!_authService.IsAuthenticated || string.IsNullOrWhiteSpace(source.Url))
        {
            return SubscriptionSyncResult.LocalOnly(snapshotPath, snapshot);
        }

        var apiResult = await SyncToApiAsync(importResult, source, subscriptionName, cancellationToken);
        if (apiResult.ServerSubscriptionId is int resolvedId && resolvedId > 0)
        {
            source.ServerSubscriptionId = resolvedId;
            snapshot.Subscriptions[0].Id = resolvedId;
            foreach (var node in snapshot.ProxyNodes)
            {
                node.SubscriptionId = resolvedId;
            }

            snapshotPath = _snapshotStore.Save(snapshot, subscriptionName);
        }

        return apiResult with { SnapshotPath = snapshotPath, Snapshot = snapshot };
    }

    private async Task<ApiResult<object?>> UpdateSubscriptionOnServerAsync(
        SubscriptionImportResult importResult,
        SubscriptionSource source,
        string subscriptionName,
        int subscriptionId,
        CancellationToken cancellationToken)
    {
        var expireAt = SubscriptionSnapshotBuilder.FormatApiExpireAt(
            SubscriptionMetadataHelper.ResolveExpireAtUtc(importResult));
        var traffic = importResult.TrafficInfo;
        var totalBytes = traffic?.TotalBytes ?? importResult.Profiles.FirstOrDefault()?.XpanelTotalBytes;
        var remainBytes = traffic?.RemainingBytes ?? importResult.Profiles.FirstOrDefault()?.XpanelRemainingBytes;

        return await _subscriptionApi.UpdateAsync(subscriptionId, new UpdateSubscriptionRequest
        {
            Name = subscriptionName,
            TotalBytes = totalBytes,
            RemainBytes = remainBytes,
            ExpireAt = expireAt
        }, cancellationToken);
    }

    private async Task<SubscriptionSyncResult> SyncToApiAsync(
        SubscriptionImportResult importResult,
        SubscriptionSource source,
        string subscriptionName,
        CancellationToken cancellationToken)
    {
        ResolveAndAssignServerSubscriptionId(source, subscriptionName);

        if (source.ServerSubscriptionId is int resolvedId && resolvedId > 0)
        {
            var updateResult = await UpdateSubscriptionOnServerAsync(
                importResult,
                source,
                subscriptionName,
                resolvedId,
                cancellationToken);

            if (!updateResult.IsSuccess)
            {
                return SubscriptionSyncResult.Fail(updateResult.Message, resolvedId);
            }

            SaveSubscriptionToSession(resolvedId, subscriptionName, source.Url);
            return SubscriptionSyncResult.Ok(resolvedId, updateResult.Message);
        }

        var expireAt = SubscriptionSnapshotBuilder.FormatApiExpireAt(
            SubscriptionMetadataHelper.ResolveExpireAtUtc(importResult));
        var traffic = importResult.TrafficInfo;
        var totalBytes = traffic?.TotalBytes ?? importResult.Profiles.FirstOrDefault()?.XpanelTotalBytes;
        var remainBytes = traffic?.RemainingBytes ?? importResult.Profiles.FirstOrDefault()?.XpanelRemainingBytes;

        var existing = await _subscriptionApi.FindByUrlAsync(source.Url, cancellationToken);
        if (existing is not null)
        {
            source.ServerSubscriptionId = existing.Id;
            var updateResult = await UpdateSubscriptionOnServerAsync(
                importResult,
                source,
                subscriptionName,
                existing.Id,
                cancellationToken);

            if (!updateResult.IsSuccess)
            {
                return SubscriptionSyncResult.Fail(updateResult.Message, existing.Id);
            }

            SaveSubscriptionToSession(existing.Id, subscriptionName, source.Url);
            return SubscriptionSyncResult.Ok(existing.Id, updateResult.Message);
        }

        var createResult = await _subscriptionApi.CreateAsync(new CreateSubscriptionRequest
        {
            Name = subscriptionName,
            Url = source.Url,
            TotalBytes = totalBytes,
            RemainBytes = remainBytes,
            ExpireAt = expireAt
        }, cancellationToken);

        if (!createResult.IsSuccess && createResult.Code != 10001)
        {
            return SubscriptionSyncResult.Fail(createResult.Message);
        }

        existing = await _subscriptionApi.FindByUrlAsync(source.Url, cancellationToken);
        if (existing is null)
        {
            return SubscriptionSyncResult.Ok(null, "订阅已创建，但未获取到服务端 ID。");
        }

        source.ServerSubscriptionId = existing.Id;
        var createdUpdateResult = await UpdateSubscriptionOnServerAsync(
            importResult,
            source,
            subscriptionName,
            existing.Id,
            cancellationToken);

        if (!createdUpdateResult.IsSuccess)
        {
            return SubscriptionSyncResult.Fail(createdUpdateResult.Message, existing.Id);
        }

        SaveSubscriptionToSession(existing.Id, subscriptionName, source.Url);
        return SubscriptionSyncResult.Ok(existing.Id, createdUpdateResult.Message);
    }

    private void SaveSubscriptionToSession(int id, string name, string url)
    {
        _authService.AddOrUpdateSubscriptionInSession(new ServerSubscription
        {
            Id = id,
            Name = name,
            Url = url
        });
    }

    public async Task PullServerSubscriptionsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!await _authService.TryRestoreSessionAsync(cancellationToken))
        {
            return;
        }

        var fetchResult = await FetchLoginSubscriptionsAsync(cancellationToken);
        if (!fetchResult.RetrievedFromServer || fetchResult.Subscriptions.Count == 0)
        {
            return;
        }

        foreach (var serverSubscription in fetchResult.Subscriptions)
        {
            var matchedKey = settings.SubscriptionSources
                .FirstOrDefault(pair => string.Equals(pair.Value.Url, serverSubscription.Url, StringComparison.OrdinalIgnoreCase))
                .Key;

            if (!string.IsNullOrWhiteSpace(matchedKey))
            {
                settings.SubscriptionSources[matchedKey].ServerSubscriptionId = serverSubscription.Id;
                settings.SubscriptionSources[matchedKey].DisplayName = serverSubscription.Name;
                continue;
            }

            settings.SubscriptionSources[serverSubscription.Name] = new SubscriptionSource
            {
                Url = serverSubscription.Url,
                ServerSubscriptionId = serverSubscription.Id,
                DisplayName = serverSubscription.Name,
                CreatedAtUtc = DateTime.UtcNow
            };
        }
    }
}

public sealed record SubscriptionSyncResult(
    bool Success,
    string Message,
    int? ServerSubscriptionId,
    string? SnapshotPath,
    SubscriptionSnapshot? Snapshot)
{
    public static SubscriptionSyncResult Ok(int? serverSubscriptionId, string message) =>
        new(true, message, serverSubscriptionId, null, null);

    public static SubscriptionSyncResult Fail(string message, int? serverSubscriptionId = null) =>
        new(false, message, serverSubscriptionId, null, null);

    public static SubscriptionSyncResult LocalOnly(string snapshotPath, SubscriptionSnapshot snapshot) =>
        new(true, "本地解析完成。", null, snapshotPath, snapshot);
}
