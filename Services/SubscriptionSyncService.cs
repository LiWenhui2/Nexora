using NaiwaProxy.Models;

namespace NaiwaProxy.Services;

public sealed class LoginSubscriptionLoadItem
{
    public required ServerSubscription Subscription { get; init; }
    public SubscriptionImportResult? ImportResult { get; init; }
    public string? ErrorMessage { get; init; }

    public bool Success => ImportResult is not null;
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
        var results = new List<LoginSubscriptionLoadItem>();
        foreach (var subscription in subscriptions)
        {
            if (string.IsNullOrWhiteSpace(subscription.Url))
            {
                results.Add(new LoginSubscriptionLoadItem
                {
                    Subscription = subscription,
                    ErrorMessage = "订阅链接为空，已跳过。"
                });
                continue;
            }

            try
            {
                var importResult = await SubscriptionImportService.ImportAsync(subscription.Url, cancellationToken);
                var normalized = NormalizeImportResult(importResult, subscription);
                results.Add(new LoginSubscriptionLoadItem
                {
                    Subscription = subscription,
                    ImportResult = normalized
                });

                DiagnosticLogService.Info(
                    $"Loaded subscription \"{subscription.Name}\" with {normalized.Profiles.Count} node(s).");
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Warning($"Failed to load subscription \"{subscription.Name}\": {ex.Message}");
                results.Add(new LoginSubscriptionLoadItem
                {
                    Subscription = subscription,
                    ErrorMessage = ex.Message
                });
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<LoginSubscriptionLoadItem>> LoadLoginSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var subscriptions = _authService.CurrentSession?.Subscriptions ?? [];
        if (subscriptions.Count == 0 && _authService.IsAuthenticated)
        {
            var listResult = await _subscriptionApi.ListAsync(cancellationToken);
            if (listResult.IsSuccess && listResult.Data is not null)
            {
                subscriptions = listResult.Data;
            }
        }

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

        return SubscriptionSyncResult.Ok(existing.Id, createdUpdateResult.Message);
    }

    public async Task PullServerSubscriptionsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!_authService.IsAuthenticated)
        {
            return;
        }

        var subscriptions = _authService.CurrentSession?.Subscriptions ?? [];
        if (subscriptions.Count == 0)
        {
            var result = await _subscriptionApi.ListAsync(cancellationToken);
            if (!result.IsSuccess || result.Data is null)
            {
                return;
            }

            subscriptions = result.Data;
        }

        foreach (var serverSubscription in subscriptions)
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
