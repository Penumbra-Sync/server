﻿using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;
using Microsoft.AspNetCore.SignalR;
using System.Globalization;
using MareSynchronos.API;
using MareSynchronosServer.Utils;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private async Task<List<PausedEntry>> GetAllPairedClientsWithPauseState(string? uid = null)
    {
        uid ??= AuthenticatedUserId;

        var query = await (from userPair in _dbContext.ClientPairs
                           join otherUserPair in _dbContext.ClientPairs on userPair.OtherUserUID equals otherUserPair.UserUID
                           where otherUserPair.OtherUserUID == uid && userPair.UserUID == uid
                           select new
                           {
                               UID = Convert.ToString(userPair.OtherUserUID),
                               GID = "DIRECT",
                               PauseState = (userPair.IsPaused || otherUserPair.IsPaused)
                           })
                            .Union(
                                (from userGroupPair in _dbContext.GroupPairs
                                 join otherGroupPair in _dbContext.GroupPairs on userGroupPair.GroupGID equals otherGroupPair.GroupGID
                                 where
                                     userGroupPair.GroupUserUID == uid
                                     && otherGroupPair.GroupUserUID != uid
                                 select new
                                 {
                                     UID = Convert.ToString(otherGroupPair.GroupUserUID),
                                     GID = Convert.ToString(otherGroupPair.GroupGID),
                                     PauseState = (userGroupPair.IsPaused || otherGroupPair.IsPaused)
                                 })
                            ).ToListAsync().ConfigureAwait(false);

        return query.GroupBy(g => g.UID, g => (g.GID, g.PauseState),
            (key, g) => new PausedEntry
            {
                UID = key,
                PauseStates = g.Select(p => new PauseState() { GID = string.Equals(p.GID, "DIRECT", StringComparison.Ordinal) ? null : p.GID, IsPaused = p.PauseState })
                .ToList()
            }, StringComparer.Ordinal).ToList();
    }

    private async Task<List<string>> GetAllPairedUnpausedUsers(string? uid = null)
    {
        uid ??= AuthenticatedUserId;
        var ret = await GetAllPairedClientsWithPauseState(uid).ConfigureAwait(false);
        return ret.Where(k => !k.IsPaused).Select(k => k.UID).ToList();
    }

    private async Task<List<string>> SendDataToAllPairedUsers(string apiMethod, object arg)
    {
        var usersToSendDataTo = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        await Clients.Users(usersToSendDataTo).SendAsync(apiMethod, arg).ConfigureAwait(false);

        return usersToSendDataTo;
    }

    public string AuthenticatedUserId => Context.User?.Claims?.SingleOrDefault(c => string.Equals(c.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal))?.Value ?? "Unknown";

    protected async Task<User> GetAuthenticatedUserUntrackedAsync()
    {
        return await _dbContext.Users.AsNoTrackingWithIdentityResolution().SingleAsync(u => u.UID == AuthenticatedUserId).ConfigureAwait(false);
    }

    private async Task UserGroupLeave(GroupPair groupUserPair, List<PausedEntry> allUserPairs, string userIdent, string? uid = null)
    {
        uid ??= AuthenticatedUserId;
        var userPair = allUserPairs.SingleOrDefault(p => string.Equals(p.UID, groupUserPair.GroupUserUID, StringComparison.Ordinal));
        if (userPair != null)
        {
            if (userPair.IsDirectlyPaused != PauseInfo.NoConnection) return;
            if (userPair.IsPausedPerGroup is PauseInfo.Unpaused) return;
        }

        var groupUserIdent = await _clientIdentService.GetCharacterIdentForUid(groupUserPair.GroupUserUID).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(groupUserIdent))
        {
            await Clients.User(uid).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, groupUserIdent).ConfigureAwait(false);
            await Clients.User(groupUserPair.GroupUserUID).SendAsync(Api.OnUserRemoveOnlinePairedPlayer, userIdent).ConfigureAwait(false);
        }
    }

    private async Task SendGroupDeletedToAll(List<GroupPair> groupUsers)
    {
        foreach (var pair in groupUsers)
        {
            var pairIdent = await _clientIdentService.GetCharacterIdentForUid(pair.GroupUserUID).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pairIdent)) continue;

            var pairs = await GetAllPairedClientsWithPauseState(pair.GroupUserUID).ConfigureAwait(false);

            foreach (var groupUserPair in groupUsers.Where(g => !string.Equals(g.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(groupUserPair, pairs, pairIdent, pair.GroupUserUID).ConfigureAwait(false);
            }
        }
    }

}
