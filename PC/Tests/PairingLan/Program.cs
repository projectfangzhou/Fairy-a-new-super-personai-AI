using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MyAiAssistant.Services;
using MyAiAssistant.Services.Pairing;
using MyAiAssistant.Services.Transfer;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var workspace = Path.Combine(AppContext.BaseDirectory, "pairing-lan-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(workspace);

try
{
    var protector = new XorProtector();
    using var service = new PairingService(
        new DeviceGroupStore(Path.Combine(workspace, "host"), protector),
        new PairingInviteStore());
    service.HostDisplayName = "测试主机";

    var (groupCreated, _, group) = service.Store.CreateGroup("家用");
    Assert(groupCreated && group is not null, "the host has a group to invite into");
    var groupId = group!.GroupId;

    // Port 0 so the test never collides with a real listener; the real app binds every interface.
    var endpoint = new PairingEndpointServer(service, port: 0);
    Assert(endpoint.Start(IPAddress.Loopback), $"the pairing port opens: {endpoint.LastError}");
    var baseUrl = $"http://127.0.0.1:{endpoint.Port}";
    Assert(endpoint.Port > 0, "the endpoint reports the port it actually bound");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    // ---- 1. A device joins over the network for real ----
    var invite = service.CreateInvite(groupId).Invite ?? throw new Exception("no invite was created");

    using var phone = DeviceIdentity.Create();
    using var joiner = new PairingJoinerSession(phone, invite.InviteId, groupId, "手机");
    var requestJson = JsonSerializer.Serialize(new { code = invite.Code, request = joiner.Request }, camel);

    var response = await http.PostAsync($"{baseUrl}/pairing/join",
        new StringContent(requestJson, Encoding.UTF8, "application/json"));
    var responseBody = await response.Content.ReadAsStringAsync();
    Assert(response.StatusCode == HttpStatusCode.OK, $"the join is accepted over the wire ({response.StatusCode}: {responseBody})");

    using var document = JsonDocument.Parse(responseBody);
    Assert(document.RootElement.GetProperty("ok").GetBoolean(), "the answer says ok");
    var answer = document.RootElement.GetProperty("response").Deserialize<PairingResponse>(camel)!;

    var joined = joiner.Accept(invite.Code, answer);
    Assert(joined.Ok, $"the joining side completes with the code: {joined.Status.Message}");
    Assert(joined.GroupKey is not null, "the joining side now holds a group key");

    var hostKey = service.Store.TryGetGroupKey(groupId)!;
    Assert(joined.GroupKey!.SequenceEqual(hostKey), "both sides derived the same group key over the network");
    Assert(!responseBody.Contains(Convert.ToBase64String(hostKey), StringComparison.Ordinal),
        "the group key does not travel across the wire in the clear");

    var stored = service.Store.Find(groupId)!;
    Assert(stored.Members.Count == 1, "the host recorded exactly one member");
    Assert(stored.Members[0].DeviceId == phone.DeviceId, "the member is the device that joined, by key");
    Assert(stored.Members[0].DisplayName == "手机", "the label travels with the request");
    Assert(stored.Members[0].Capabilities.Count == 0, "joining grants no capability");
    Assert(!responseBody.Contains(invite.Code, StringComparison.Ordinal),
        "the answer never echoes the pairing code");

    // ---- 2. The invite is spent: the same code cannot bring in a second device ----
    using var tablet = DeviceIdentity.Create();
    using var replay = new PairingJoinerSession(tablet, invite.InviteId, groupId, "平板");
    var replayResponse = await http.PostAsync($"{baseUrl}/pairing/join",
        new StringContent(JsonSerializer.Serialize(new { code = invite.Code, request = replay.Request }, camel),
            Encoding.UTF8, "application/json"));
    Assert(replayResponse.StatusCode == HttpStatusCode.Forbidden, "a used invite is refused");
    using (var replayBody = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync()))
    {
        Assert(replayBody.RootElement.GetProperty("error").GetString() == PairingErrors.InviteConsumed,
            "the refusal names the spent invite");
    }

    Assert(service.Store.Find(groupId)!.Members.Count == 1, "the replayed attempt added nobody");

    // ---- 3. A wrong code is refused, and nobody is added ----
    var secondInvite = service.CreateInvite(groupId).Invite!;
    using var other = new PairingJoinerSession(tablet, secondInvite.InviteId, groupId, "平板");
    var wrongCode = secondInvite.Code == "000000" ? "111111" : "000000";
    var wrongResponse = await http.PostAsync($"{baseUrl}/pairing/join",
        new StringContent(JsonSerializer.Serialize(new { code = wrongCode, request = other.Request }, camel),
            Encoding.UTF8, "application/json"));
    Assert(wrongResponse.StatusCode == HttpStatusCode.Forbidden, "a wrong code is refused");
    using (var wrongBody = JsonDocument.Parse(await wrongResponse.Content.ReadAsStringAsync()))
    {
        Assert(wrongBody.RootElement.GetProperty("error").GetString() == PairingErrors.CodeMismatch,
            "the refusal says the code was wrong");
    }

    Assert(service.Store.Find(groupId)!.Members.Count == 1, "a refused join leaves the group unchanged");

    // ---- 4. The same device cannot be added twice, even with a fresh code ----
    var thirdInvite = service.CreateInvite(groupId).Invite!;
    using var duplicate = new PairingJoinerSession(phone, thirdInvite.InviteId, groupId, "手机");
    var duplicateResponse = await http.PostAsync($"{baseUrl}/pairing/join",
        new StringContent(JsonSerializer.Serialize(new { code = thirdInvite.Code, request = duplicate.Request }, camel),
            Encoding.UTF8, "application/json"));
    Assert(duplicateResponse.StatusCode == HttpStatusCode.Forbidden, "re-pairing a device already in the group is refused");
    using (var duplicateBody = JsonDocument.Parse(await duplicateResponse.Content.ReadAsStringAsync()))
    {
        Assert(duplicateBody.RootElement.GetProperty("error").GetString() == PairingErrors.DuplicateMember,
            "the refusal says the device is already a member");
    }

    Assert(service.Store.Find(groupId)!.Members.Count == 1, "the duplicate did not add a second entry");

    // ---- 5. Nothing else is exposed ----
    var otherPath = await http.PostAsync($"{baseUrl}/api/pairing", new StringContent("{}", Encoding.UTF8, "application/json"));
    Assert(otherPath.StatusCode == HttpStatusCode.NotFound, "only the join path exists");

    var getJoin = await http.GetAsync($"{baseUrl}/pairing/join");
    Assert(getJoin.StatusCode == HttpStatusCode.NotFound, "the join path only answers POST");

    var missingCode = await http.PostAsync($"{baseUrl}/pairing/join",
        new StringContent(JsonSerializer.Serialize(new { request = phone.DeviceId }, camel), Encoding.UTF8, "application/json"));
    Assert(missingCode.StatusCode == HttpStatusCode.BadRequest, "a body without a code is a client error");

    var malformed = await http.PostAsync($"{baseUrl}/pairing/join",
        new StringContent("not json", Encoding.UTF8, "application/json"));
    Assert(malformed.StatusCode == HttpStatusCode.BadRequest, "malformed JSON is a client error");

    // ---- 6. A body far beyond the limit is dropped, not buffered ----
    var oversized = new string('x', 40 * 1024);
    var oversizedRefused = false;
    try
    {
        var huge = await http.PostAsync($"{baseUrl}/pairing/join", new StringContent(oversized, Encoding.UTF8, "application/json"));
        oversizedRefused = huge.StatusCode != HttpStatusCode.OK;
    }
    catch (HttpRequestException)
    {
        // The server closes the connection instead of reading the body, which is the point.
        oversizedRefused = true;
    }

    Assert(oversizedRefused, "an oversized body never reaches the handler");

    endpoint.Stop();

    // ---- 7. Guessing is rate limited per address, on top of the invite's own attempt cap ----
    using var limitedService = new PairingService(
        new DeviceGroupStore(Path.Combine(workspace, "limited"), protector),
        new PairingInviteStore());
    var (limitedCreated, _, limitedGroup) = limitedService.Store.CreateGroup("限速");
    Assert(limitedCreated, "the rate-limit group exists");

    var limitedEndpoint = new PairingEndpointServer(limitedService, port: 0);
    Assert(limitedEndpoint.Start(IPAddress.Loopback), "the rate-limit endpoint opens");
    var limitedUrl = $"http://127.0.0.1:{limitedEndpoint.Port}/pairing/join";

    var lastStatus = HttpStatusCode.OK;
    for (var attempt = 0; attempt < 21; attempt++)
    {
        // A fresh invite per attempt, so the limit being measured is the address's, not the invite's.
        var fresh = limitedService.CreateInvite(limitedGroup!.GroupId).Invite!;
        using var session = new PairingJoinerSession(DeviceIdentity.Create(), fresh.InviteId, fresh.GroupId, "对手");
        var body = JsonSerializer.Serialize(new { code = "000000", request = session.Request }, camel);
        var attemptResponse = await http.PostAsync(limitedUrl, new StringContent(body, Encoding.UTF8, "application/json"));
        lastStatus = attemptResponse.StatusCode;
        if (attempt < 20) Assert(lastStatus == HttpStatusCode.Forbidden, $"guess {attempt + 1} is refused as a wrong code");
    }

    Assert(lastStatus == HttpStatusCode.TooManyRequests, "the twenty-first request from one address is throttled");

    limitedEndpoint.Stop();

    // ---- 9. The relay can also serve its own mailbox, which is how this machine sends ----
    {
        using var hostService = new PairingService(
            new DeviceGroupStore(Path.Combine(workspace, "host-mailbox"), protector),
            new PairingInviteStore());
        var (mailboxGroupCreated, _, mailboxGroup) = hostService.Store.CreateGroup("本机组");
        Assert(mailboxGroupCreated, "the mailbox group exists");

        var mailboxGroupId = mailboxGroup!.GroupId;
        var member = AddMember(hostService, mailboxGroupId, "手机");
        var hostId = hostService.Store.Identity.DeviceId;
        var mailboxKey = hostService.Store.TryGetGroupKey(mailboxGroupId)!;

        var mailboxEndpoint = new PairingEndpointServer(hostService, port: 0);
        Assert(mailboxEndpoint.Start(IPAddress.Loopback), "the mailbox endpoint opens");

        // Host → member: the offer this machine produces for a device it can reach.
        var offerId = mailboxEndpoint.EnqueueSignal(mailboxGroupId, member, hostId,
            new TransferSignal(TransferSignalKind.Offer, "{\"sdp\":\"host-offer\"}"));
        Assert(offerId > 0, "the host can put a sealed signal in a member's inbox");

        using var memberClient = new SignalClient($"http://127.0.0.1:{mailboxEndpoint.Port}",
            mailboxGroupId, member, hostId, mailboxKey);
        var memberPoll = await memberClient.PollAsync();
        Assert(memberPoll.Ok && memberPoll.Signals.Count == 1, $"the member receives it over the network: {memberPoll.Error}");
        Assert(memberPoll.Signals[0].From == hostId, "and it is addressed from the host");
        Assert(memberPoll.Signals[0].Signal.Payload.Contains("host-offer", StringComparison.Ordinal),
            "the payload is intact");

        // Member → host: the answer, read straight out of the host's own mailbox.
        var answerId = mailboxEndpoint.EnqueueSignal(mailboxGroupId, hostId, member,
            new TransferSignal(TransferSignalKind.Answer, "{\"sdp\":\"member-answer\"}"));
        Assert(answerId > offerId, "delivery ids keep increasing across both directions");

        long since = 0;
        var hostSignals = mailboxEndpoint.TakeSignals(mailboxGroupId, hostId, ref since);
        Assert(hostSignals.Count == 1, "the host reads its own inbox without the network");
        Assert(hostSignals[0].From == member, "the signal names the member");
        Assert(hostSignals[0].Signal.Payload.Contains("member-answer", StringComparison.Ordinal),
            "the host gets the answer it was waiting for");
        Assert(since == answerId, "reading advances the cursor");

        var again = mailboxEndpoint.TakeSignals(mailboxGroupId, hostId, ref since);
        Assert(again.Count == 0, "reading twice does not replay the same signal");

        mailboxEndpoint.Stop();
    }

    // ---- 8. Two paired members exchange signalling through the host, sealed with the group key ----
    {
        using var signalService = new PairingService(
            new DeviceGroupStore(Path.Combine(workspace, "signal-host"), protector),
            new PairingInviteStore());
        var (signalGroupCreated, _, signalGroup) = signalService.Store.CreateGroup("传输组");
        Assert(signalGroupCreated, "the signalling group exists");
        var signalGroupId = signalGroup!.GroupId;

        var deviceA = AddMember(signalService, signalGroupId, "笔记本");
        var deviceB = AddMember(signalService, signalGroupId, "平板");
        var outsider = DeviceIdentity.Create();
        var hostDeviceId = signalService.Store.Identity.DeviceId;
        var groupKey = signalService.Store.TryGetGroupKey(signalGroupId)!;

        var signalEndpoint = new PairingEndpointServer(signalService, port: 0);
        Assert(signalEndpoint.Start(IPAddress.Loopback), "the signalling endpoint opens");
        var signalUrl = $"http://127.0.0.1:{signalEndpoint.Port}";

        using var clientA = new SignalClient(signalUrl, signalGroupId, deviceA, hostDeviceId, groupKey);
        using var clientB = new SignalClient(signalUrl, signalGroupId, deviceB, hostDeviceId, groupKey);

        var offer = new TransferSignal(TransferSignalKind.Offer, "{\"sdp\":\"v=0\\r\\no=- 1 1 IN IP4 127.0.0.1\"}");
        var sent = await clientA.SendAsync(deviceB, offer);
        Assert(sent.Ok, $"a member can post a signal: {sent.Error}");

        var firstPoll = await clientB.PollAsync();
        Assert(firstPoll.Ok, $"the addressed member can poll: {firstPoll.Error}");
        Assert(firstPoll.Signals.Count == 1, "exactly one signal is waiting");
        Assert(firstPoll.Signals[0].From == deviceA, "the signal names its sender");
        Assert(firstPoll.Signals[0].Signal.Payload.Contains("v=0", StringComparison.Ordinal),
            "the SDP survived the round trip through the host");
        Assert(firstPoll.Signals[0].Signal.Kind == TransferSignalKind.Offer, "the signal kind survives too");

        var secondPoll = await clientB.PollAsync();
        Assert(secondPoll.Ok && secondPoll.Signals.Count == 0, "polling again does not redeliver the same signal");

        var back = await clientB.SendAsync(deviceA, new TransferSignal(TransferSignalKind.Answer, "{\"sdp\":\"v=0\"}"));
        Assert(back.Ok, "signalling works in the other direction");
        var answered = await clientA.PollAsync();
        Assert(answered.Ok && answered.Signals.Count == 1 && answered.Signals[0].Signal.Kind == TransferSignalKind.Answer,
            "the answer reaches the offering side");

        // A device that never paired cannot use the relay, even holding the group id.
        outsider.Dispose();
        using var outsiderClient = new SignalClient(signalUrl, signalGroupId, "DEVSTRANGER0001", hostDeviceId, groupKey);
        var strangerSend = await outsiderClient.SendAsync(deviceB, offer);
        Assert(!strangerSend.Ok, "a device that is not a member cannot post signalling");
        Assert(strangerSend.Error.Contains("不在这个设备组", StringComparison.Ordinal),
            "the refusal says it is not in the group");

        // A member with the wrong key is a member, but cannot produce a message the group accepts.
        using var wrongKeyClient = new SignalClient(signalUrl, signalGroupId, deviceB, hostDeviceId,
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var wrongKeySend = await wrongKeyClient.SendAsync(deviceA, offer);
        Assert(!wrongKeySend.Ok, "a member with the wrong group key cannot post signalling");
        var wrongKeyPoll = await wrongKeyClient.PollAsync();
        Assert(!wrongKeyPoll.Ok, "and cannot read its own inbox");

        // Replaying a captured envelope is refused: the nonce is single-use.
        var replayEnvelope = TransferSignals.Seal(groupKey, signalGroupId, deviceA, deviceB,
            new TransferSignal(TransferSignalKind.Ice, "{\"candidate\":\"x\"}"), DateTimeOffset.UtcNow);
        var replayBody = new StringContent(replayEnvelope.ToJson(), Encoding.UTF8, "application/json");
        var firstPost = await http.PostAsync($"{signalUrl}/pairing/signal", replayBody);
        Assert(firstPost.StatusCode == HttpStatusCode.OK, "the first delivery of an envelope is accepted");

        var replayAgain = await http.PostAsync($"{signalUrl}/pairing/signal",
            new StringContent(replayEnvelope.ToJson(), Encoding.UTF8, "application/json"));
        Assert(replayAgain.StatusCode == HttpStatusCode.Forbidden, "replaying the same envelope is refused");

        // Nothing outside the group's own routes is exposed here either.
        var unknownSignalPath = await http.PostAsync($"{signalUrl}/pairing/signal/all",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert(unknownSignalPath.StatusCode == HttpStatusCode.NotFound, "only the named signalling routes exist");

        signalEndpoint.Stop();
    }

    Console.WriteLine($"All {checks} pairing-over-network checks passed.");
    return 0;
}
finally
{
    try { Directory.Delete(workspace, true); } catch (IOException) { }
}

/// <summary>Puts a device in the group the way a successful pairing would, without a live join.</summary>
static string AddMember(PairingService service, string groupId, string name)
{
    using var identity = DeviceIdentity.Create();
    var request = new PairingRequest("INVSEED", groupId, identity.DeviceId, name, identity.SigningPublicKey,
        identity.AgreementPublicKey, identity.AgreementPublicKey,
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));

    var (ok, message, member) = service.Store.AddMember(groupId, request);
    if (!ok || member is null) throw new Exception("could not seed member: " + message);
    return member.DeviceId;
}

/// <summary>Reversible stand-in for ApiKeyProtector: the tests must not touch the machine's keys.</summary>
internal sealed class XorProtector : IPairingSecretProtector
{
    public string Protect(string plaintext) => Convert.ToBase64String(Scramble(Encoding.UTF8.GetBytes(plaintext)));

    public string Unprotect(string ciphertext) => Encoding.UTF8.GetString(Scramble(Convert.FromBase64String(ciphertext)));

    private static byte[] Scramble(byte[] input)
    {
        var output = new byte[input.Length];
        for (var index = 0; index < input.Length; index++) output[index] = (byte)(input[index] ^ 0x5A);
        return output;
    }
}
