using System.IO;
using System.Security.Cryptography;
using System.Text;
using MyAiAssistant.Services.Transfer;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

var key = RandomNumberGenerator.GetBytes(32);
var otherKey = RandomNumberGenerator.GetBytes(32);
var workspace = Path.Combine(AppContext.BaseDirectory, "transfer-" + Guid.NewGuid().ToString("N"));
var source = Path.Combine(workspace, "source");
var destination = Path.Combine(workspace, "destination");
Directory.CreateDirectory(source);
Directory.CreateDirectory(destination);

try
{
    // ---- 1. The frame format itself ----
    {
        var payload = Encoding.UTF8.GetBytes("分块内容");
        var frame = TransferFrames.Encode(TransferFrameKind.Chunk, 3, 17, payload, key);

        Assert(TransferFrames.TryDecode(frame, key, out var decoded, out var error), $"a frame decodes: {error}");
        Assert(decoded!.Kind == TransferFrameKind.Chunk, "the kind survives");
        Assert(decoded.FileIndex == 3 && decoded.ChunkIndex == 17, "the indices survive");
        Assert(decoded.Payload.SequenceEqual(payload), "the payload survives, non-ASCII included");

        var tampered = (byte[])frame.Clone();
        tampered[^1] ^= 0xFF;
        Assert(!TransferFrames.TryDecode(tampered, key, out _, out var tamperError), "a flipped tag byte is refused");
        Assert(tamperError.Contains("校验失败", StringComparison.Ordinal), "the refusal says the frame failed authentication");

        var relabelled = (byte[])frame.Clone();
        relabelled[8] ^= 0x01; // pretend this is another chunk
        Assert(!TransferFrames.TryDecode(relabelled, key, out _, out _),
            "a frame whose header was edited is refused, so content cannot be re-labelled");

        Assert(!TransferFrames.TryDecode(frame, otherKey, out _, out var keyError), "another group key cannot open the frame");
        Assert(keyError.Contains("校验失败", StringComparison.Ordinal), "the wrong-key refusal is the same refusal");

        Assert(!TransferFrames.TryDecode(frame[..10], key, out _, out _), "a truncated frame is refused");
        Assert(!TransferFrames.TryDecode([0x00, 0x01, 0x02, 0x03], key, out _, out _), "a non-frame is refused");

        var threw = false;
        try { TransferFrames.Encode(TransferFrameKind.Chunk, 0, 0, payload, RandomNumberGenerator.GetBytes(16)); }
        catch (ArgumentException) { threw = true; }
        Assert(threw, "a short key is refused at encode time rather than guessed at");

        var tooBig = false;
        try { TransferFrames.Encode(TransferFrameKind.Chunk, 0, 0, new byte[TransferFrames.MaxPayloadBytes + 1], key); }
        catch (ArgumentException) { tooBig = true; }
        Assert(tooBig, "an oversized payload is refused");
    }

    // ---- 2. A whole transfer, end to end ----
    var bigPath = Path.Combine(source, "样本.bin");
    var smallPath = Path.Combine(source, "note.txt");
    var bigBytes = RandomNumberGenerator.GetBytes(200 * 1024 + 123);
    var smallBytes = Encoding.UTF8.GetBytes("仙灵分块传输测试");
    File.WriteAllBytes(bigPath, bigBytes);
    File.WriteAllBytes(smallPath, smallBytes);

    {
        var sender = new TransferSender(key, [bigPath, smallPath], "台式机", chunkSize: 32 * 1024);
        using var receiver = new TransferReceiver(key, destination, chunkSize: 32 * 1024);

        Assert(receiver.Accept(sender.BuildManifestFrame()).Ok, "the manifest is accepted");
        Assert(receiver.Manifest is not null && receiver.Manifest.Files.Count == 2, "two files are announced");
        Assert(receiver.Manifest!.Files[0].Name == "样本.bin", "the file name is announced, not the path");
        Assert(!receiver.Manifest.Files[0].Name.Contains(source, StringComparison.Ordinal), "the local path never leaves the sender");

        for (var fileIndex = 0; fileIndex < 2; fileIndex++)
        {
            foreach (var frame in sender.BuildChunkFrames(fileIndex))
            {
                var result = receiver.Accept(frame);
                Assert(result.Ok, $"chunk accepted for file {fileIndex}: {result.Message}");
            }

            var complete = receiver.Accept(sender.BuildCompleteFrame(fileIndex));
            Assert(complete.Ok, $"file {fileIndex} completes: {complete.Message}");
        }

        var receivedBig = Path.Combine(destination, "样本.bin");
        var receivedSmall = Path.Combine(destination, "note.txt");
        Assert(File.Exists(receivedBig) && File.Exists(receivedSmall), "both files land in the destination");
        Assert(File.ReadAllBytes(receivedBig).SequenceEqual(bigBytes), "the large file arrives byte-for-byte");
        Assert(File.ReadAllBytes(receivedSmall).SequenceEqual(smallBytes), "the small file arrives byte-for-byte");
        Assert(receiver.Progress.All(progress => progress.Completed), "the receiver reports both as completed");
    }

    // ---- 3. An interrupted transfer resumes at the missing chunks ----
    {
        Directory.CreateDirectory(Path.Combine(workspace, "resume"));
        var resumeTarget = Path.Combine(workspace, "resume");

        var sender = new TransferSender(key, [bigPath], "台式机", chunkSize: 32 * 1024);
        using var receiver = new TransferReceiver(key, resumeTarget, chunkSize: 32 * 1024);
        Assert(receiver.Accept(sender.BuildManifestFrame()).Ok, "resume manifest accepted");

        var frames = sender.BuildChunkFrames(0).ToList();
        Assert(frames.Count > 3, "the file needs several chunks");

        // Send the first one and every chunk after the second, then pretend the link dropped.
        Assert(receiver.Accept(frames[0]).Ok, "first chunk accepted");
        for (var index = 2; index < frames.Count; index++) receiver.Accept(frames[index]);

        Assert(receiver.MissingChunks(0).SequenceEqual([1]), "the receiver names exactly the chunk it is missing");

        var premature = receiver.Accept(sender.BuildCompleteFrame(0));
        Assert(!premature.Ok && premature.Message.Contains("还缺", StringComparison.Ordinal),
            "completing with a hole is refused, and says how much is missing");
        Assert(!File.Exists(Path.Combine(resumeTarget, "样本.bin")), "nothing is published while a chunk is missing");

        Assert(receiver.Accept(frames[1]).Ok, "the missing chunk is accepted on retry");
        var complete = receiver.Accept(sender.BuildCompleteFrame(0));
        Assert(complete.Ok, $"the transfer completes after the retry: {complete.Message}");
        Assert(File.ReadAllBytes(Path.Combine(resumeTarget, "样本.bin")).SequenceEqual(bigBytes),
            "the resumed file is identical to the source");
    }

    // ---- 4. A resent chunk is not counted twice ----
    {
        Directory.CreateDirectory(Path.Combine(workspace, "duplicate"));
        var duplicateTarget = Path.Combine(workspace, "duplicate");

        var sender = new TransferSender(key, [smallPath], "台式机", chunkSize: 1024);
        using var receiver = new TransferReceiver(key, duplicateTarget, chunkSize: 1024);
        receiver.Accept(sender.BuildManifestFrame());

        var frames = sender.BuildChunkFrames(0).ToList();
        receiver.Accept(frames[0]);
        receiver.Accept(frames[0]);
        Assert(receiver.Progress[0].ReceivedChunks == 1, "a duplicate chunk does not advance the count");
    }

    // ---- 5. Names the sender chooses cannot address anything outside the destination ----
    {
        Directory.CreateDirectory(Path.Combine(workspace, "traversal"));
        var trappedTarget = Path.Combine(workspace, "traversal");

        var malicious = new TransferManifest("sess-traversal",
            [new TransferFile("../escaped.txt", 4, "00")], "对手");

        using var receiver = new TransferReceiver(key, trappedTarget);
        var refused = receiver.Accept(TransferFrames.EncodeManifest(malicious, key));
        Assert(!refused.Ok, "a manifest with a traversal name is refused");
        Assert(!File.Exists(Path.Combine(workspace, "escaped.txt")), "nothing was written outside the destination");
        Assert(receiver.Manifest is null, "the refused manifest is not adopted");

        var absolute = new TransferManifest("sess-abs",
            [new TransferFile(@"C:\Windows\evil.txt", 4, "00")], "对手");
        Assert(!receiver.Accept(TransferFrames.EncodeManifest(absolute, key)).Ok, "an absolute path is refused too");
    }

    // ---- 6. Existing files are not overwritten on a stranger's say-so ----
    {
        Directory.CreateDirectory(Path.Combine(workspace, "existing"));
        var existingTarget = Path.Combine(workspace, "existing");
        File.WriteAllText(Path.Combine(existingTarget, "note.txt"), "原有内容");

        var sender = new TransferSender(key, [smallPath], "台式机");
        using var receiver = new TransferReceiver(key, existingTarget);
        var result = receiver.Accept(sender.BuildManifestFrame());
        Assert(!result.Ok && result.Message.Contains("同名文件", StringComparison.Ordinal),
            "a transfer that would overwrite is refused up front");
        Assert(File.ReadAllText(Path.Combine(existingTarget, "note.txt")) == "原有内容", "the existing file is untouched");
    }

    // ---- 7. A file whose hash does not match is discarded ----
    {
        Directory.CreateDirectory(Path.Combine(workspace, "hash"));
        var hashTarget = Path.Combine(workspace, "hash");

        var sender = new TransferSender(key, [smallPath], "台式机", chunkSize: 1024);
        var wrong = new TransferManifest("sess-hash",
            [new TransferFile("note.txt", smallBytes.Length, new string('0', 64))], "台式机");

        using var receiver = new TransferReceiver(key, hashTarget, chunkSize: 1024);
        Assert(receiver.Accept(TransferFrames.EncodeManifest(wrong, key)).Ok, "the manifest is accepted on its own terms");

        foreach (var frame in sender.BuildChunkFrames(0)) receiver.Accept(frame);

        var complete = receiver.Accept(sender.BuildCompleteFrame(0));
        Assert(!complete.Ok && complete.Message.Contains("校验不通过", StringComparison.Ordinal),
            "a hash mismatch refuses the file");
        Assert(!File.Exists(Path.Combine(hashTarget, "note.txt")), "a file that failed its hash is never published");
    }

    // ---- 8. Frames for the wrong key are dropped without writing ----
    {
        Directory.CreateDirectory(Path.Combine(workspace, "wrongkey"));
        var wrongKeyTarget = Path.Combine(workspace, "wrongkey");

        var sender = new TransferSender(key, [smallPath], "台式机", chunkSize: 1024);
        using var receiver = new TransferReceiver(otherKey, wrongKeyTarget, chunkSize: 1024);

        var refused = receiver.Accept(sender.BuildManifestFrame());
        Assert(!refused.Ok, "a peer without the group key cannot even deliver a manifest");
        Assert(receiver.Manifest is null, "nothing is adopted from an unauthenticated sender");
    }

    // ---- 9. Limits are enforced before any byte is written ----
    {
        Directory.CreateDirectory(Path.Combine(workspace, "limits"));
        var limitTarget = Path.Combine(workspace, "limits");
        using var receiver = new TransferReceiver(key, limitTarget);

        var tooMany = new TransferManifest("sess-many",
            Enumerable.Range(0, TransferReceiver.MaxFiles + 1)
                .Select(index => new TransferFile($"f{index}.bin", 1, "00"))
                .ToArray(), "台式机");
        Assert(!receiver.Accept(TransferFrames.EncodeManifest(tooMany, key)).Ok, "too many files are refused");

        var empty = new TransferManifest("sess-empty", Array.Empty<TransferFile>(), "台式机");
        Assert(!receiver.Accept(TransferFrames.EncodeManifest(empty, key)).Ok, "an empty manifest is refused");
    }

    // ---- 10. A chunk before the manifest is refused, not written ----
    {
        Directory.CreateDirectory(Path.Combine(workspace, "orphan"));
        var orphanTarget = Path.Combine(workspace, "orphan");
        using var receiver = new TransferReceiver(key, orphanTarget, chunkSize: 1024);

        var sender = new TransferSender(key, [smallPath], "台式机", chunkSize: 1024);
        var orphan = receiver.Accept(sender.BuildChunkFrames(0).First());
        Assert(!orphan.Ok && orphan.Message.Contains("清单", StringComparison.Ordinal),
            "a chunk that arrives before the manifest is refused");
    }

    // ---- 11. Signalling envelopes: sealed to the group, addressed to one device ----
    {
        var groupId = "GRPSIGNALTEST0001";
        var now = DateTimeOffset.UtcNow;
        var signal = new TransferSignal(TransferSignalKind.Offer, "{\"sdp\":\"v=0\\r\\no=- 1 1 IN IP4 127.0.0.1\"}");

        var envelope = TransferSignals.Seal(key, groupId, "DEVA", "DEVB", signal, now);
        Assert(envelope.From == "DEVA" && envelope.To == "DEVB", "the envelope records both ends");
        Assert(!envelope.Sealed.Contains("sdp", StringComparison.OrdinalIgnoreCase),
            "the SDP is not readable in the sealed field");

        Assert(TransferSignals.TryOpenSignal(key, envelope, now, "DEVB", out var opened, out var openError),
            $"the addressed device opens it: {openError}");
        Assert(opened!.Kind == TransferSignalKind.Offer && opened.Payload == signal.Payload,
            "the signal survives sealing and opening");

        Assert(!TransferSignals.TryOpenSignal(otherKey, envelope, now, "DEVB", out _, out var keyError),
            "another group key cannot open it");
        Assert(keyError.Contains("校验失败", StringComparison.Ordinal), "the wrong-key refusal is the same refusal");

        Assert(!TransferSignals.TryOpenSignal(key, envelope, now, "DEVC", out _, out var wrongTarget),
            "a device it was not addressed to cannot open it");
        Assert(wrongTarget.Contains("不是发给", StringComparison.Ordinal), "the refusal says the message is for someone else");

        Assert(!TransferSignals.TryOpenSignal(key, envelope with { To = "DEVC" }, now, "DEVC", out _, out _),
            "re-addressing the envelope breaks the seal, so routing fields cannot be edited");

        var stale = now.AddMinutes(10);
        Assert(!TransferSignals.TryOpenSignal(key, envelope, stale, "DEVB", out _, out var staleError),
            "an old envelope is refused even with a valid seal");
        Assert(staleError.Contains("时间戳", StringComparison.Ordinal), "the refusal says the message is too old");

        var tampered = envelope with { Sealed = Flip(envelope.Sealed) };
        Assert(!TransferSignals.TryOpenSignal(key, tampered, now, "DEVB", out _, out _), "a tampered envelope is refused");

        Assert(SignalEnvelope.Parse("not json") is null, "a malformed envelope is refused, not thrown");
        Assert(SignalEnvelope.Parse(envelope.ToJson()) is not null, "an envelope survives serialisation");

        var control = TransferSignals.SealControl(key, groupId, "DEVA", "HOST", "poll", "{\"since\":7}", now);
        Assert(TransferSignals.TryOpen(key, control, now, "HOST", out var kindText, out var valueText, out _),
            "a control request opens like any other envelope");
        Assert(kindText == "poll" && valueText.Contains("7", StringComparison.Ordinal), "the control kind and value survive");
        Assert(!TransferSignals.TryOpenSignal(key, control, now, "HOST", out _, out _),
            "a control request is not mistaken for a connection signal");
    }

    // ---- 12. Replay guard and mailbox behaviour ----
    {
        var guard = new SignalReplayGuard();
        Assert(guard.Accept("G", "DEVA", "nonce-1"), "a nonce is accepted the first time");
        Assert(!guard.Accept("G", "DEVA", "nonce-1"), "the same nonce is refused the second time");
        Assert(guard.Accept("G", "DEVA", "nonce-2"), "a fresh nonce is accepted");
        Assert(guard.Accept("G", "DEVB", "nonce-1"), "the same nonce from another device is a different message");

        var clock = DateTimeOffset.UtcNow;
        var mailbox = new SignalMailbox(() => clock);
        var first = mailbox.Post("G", "DEVB", "DEVA", "{\"payload\":1}");
        var second = mailbox.Post("G", "DEVB", "DEVA", "{\"payload\":2}");
        Assert(second > first, "delivery ids increase");

        Assert(mailbox.Since("G", "DEVB", 0).Count == 2, "both messages wait for the recipient");
        Assert(mailbox.Since("G", "DEVB", first).Count == 1, "asking past an id returns only what is newer");
        Assert(mailbox.Since("G", "DEVC", 0).Count == 0, "another device's inbox is separate");

        clock = clock.Add(SignalMailbox.Ttl + TimeSpan.FromSeconds(1));
        Assert(mailbox.Since("G", "DEVB", 0).Count == 0, "expired signalling is dropped");

        var bulkMailbox = new SignalMailbox();
        for (var index = 0; index < SignalMailbox.MaxPerRecipient + 10; index++)
            bulkMailbox.Post("G", "DEVB", "DEVA", "{}");
        Assert(bulkMailbox.Count("G", "DEVB") == SignalMailbox.MaxPerRecipient,
            "a peer that never polls cannot grow the mailbox without bound");

        bulkMailbox.ClearGroup("G");
        Assert(bulkMailbox.Count("G", "DEVB") == 0, "a group's mailbox can be cleared");
    }

    // ---- 13. A whole session: approval gates the write, then the file lands intact ----
    {
        var destA = Path.Combine(workspace, "session-a");
        var destB = Path.Combine(workspace, "session-b");
        Directory.CreateDirectory(destA);
        Directory.CreateDirectory(destB);

        var payload = RandomNumberGenerator.GetBytes(150 * 1024 + 77);
        var sendPath = Path.Combine(source, "会话样本.bin");
        File.WriteAllBytes(sendPath, payload);

        var channelA = new LoopbackChannel();
        var channelB = new LoopbackChannel();
        channelA.Peer = channelB;
        channelB.Peer = channelA;

        TransferOffer? offer = null;
        await using var receiver = new TransferSession(channelB, key, destB,
            incoming => { offer = incoming; return Task.FromResult(true); }, chunkSize: 16 * 1024);
        await using var sender = new TransferSession(channelA, key, destA, null, chunkSize: 16 * 1024);

        Assert(await sender.OpenAsync(WebRtcRole.Offerer), "the sending session opens");
        Assert(await receiver.OpenAsync(WebRtcRole.Answerer), "the receiving session opens");

        var saved = new TaskCompletionSource<TransferFileProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileReceived += progress => saved.TrySetResult(progress);
        var refusals = new List<string>();
        receiver.Refused += refusal => refusals.Add(refusal.Message);

        var bytes = await sender.SendFilesAsync([sendPath], "台式机");
        Assert(bytes > payload.Length, "the session reports the bytes it put on the wire");

        Assert(await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(20))) == saved.Task,
            "the file is reported as received: " + string.Join("; ", refusals));
        Assert(saved.Task.Result.File.Name == "会话样本.bin", "the received file keeps its name");
        Assert(File.ReadAllBytes(Path.Combine(destB, "会话样本.bin")).SequenceEqual(payload),
            "the received file is identical to the source");
        Assert(offer is not null, "the receiver was asked before anything was written");
        Assert(offer!.TotalBytes == payload.Length, "the offer states the real size");
        Assert(offer.Describe().Contains("台式机", StringComparison.Ordinal), "the offer names the sender");
        Assert(File.Exists(Path.Combine(destB, "会话样本.bin")), "the file is published under its real name");
        Assert(receiver.Progress.All(progress => progress.Completed), "progress reports completion");
    }

    // ---- 14. No approval means no write, and a refusal travels back ----
    {
        var destA = Path.Combine(workspace, "refuse-a");
        var destB = Path.Combine(workspace, "refuse-b");
        Directory.CreateDirectory(destA);
        Directory.CreateDirectory(destB);

        var sendPath = Path.Combine(source, "refused.bin");
        File.WriteAllBytes(sendPath, RandomNumberGenerator.GetBytes(32 * 1024));

        var channelA = new LoopbackChannel();
        var channelB = new LoopbackChannel();
        channelA.Peer = channelB;
        channelB.Peer = channelA;

        var asked = 0;
        await using var receiver = new TransferSession(channelB, key, destB,
            _ => { asked++; return Task.FromResult(false); }, chunkSize: 8 * 1024);
        await using var sender = new TransferSession(channelA, key, destA, null, chunkSize: 8 * 1024);

        await sender.OpenAsync(WebRtcRole.Offerer);
        await receiver.OpenAsync(WebRtcRole.Answerer);

        var receiverRefused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Refused += refusal => receiverRefused.TrySetResult(refusal.Message);
        var senderRefused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.Refused += refusal => senderRefused.TrySetResult(refusal.Message);

        try
        {
            await sender.SendFilesAsync([sendPath], "台式机");
            Assert(false, "a declined transfer should not report success");
        }
        catch (InvalidOperationException)
        {
            Assert(true, "the sending side stops as soon as the transfer is declined");
        }

        Assert(asked == 1, "the user was asked exactly once");
        Assert(await Task.WhenAny(receiverRefused.Task, Task.Delay(TimeSpan.FromSeconds(10))) == receiverRefused.Task,
            "the receiver reports the refusal");
        Assert(receiver.IncomingManifest is null, "a refused manifest is never adopted");
        Assert(!File.Exists(Path.Combine(destB, "refused.bin")), "a refused transfer writes nothing");
        Assert(!File.Exists(Path.Combine(destB, ".incoming")), "not even a session directory is created");
        Assert(await Task.WhenAny(senderRefused.Task, Task.Delay(TimeSpan.FromSeconds(10))) == senderRefused.Task,
            "the sending side learns it was refused");
        Assert(senderRefused.Task.Result.Contains("取消", StringComparison.Ordinal) ||
               senderRefused.Task.Result.Contains("没有被接受", StringComparison.Ordinal),
            "the sender's message says the transfer stopped: " + senderRefused.Task.Result);
    }

    // ---- 15. With no way to ask, the session refuses rather than accepting silently ----
    {
        var destA = Path.Combine(workspace, "silent-a");
        var destB = Path.Combine(workspace, "silent-b");
        Directory.CreateDirectory(destA);
        Directory.CreateDirectory(destB);

        var sendPath = Path.Combine(source, "silent.bin");
        File.WriteAllBytes(sendPath, RandomNumberGenerator.GetBytes(4 * 1024));

        var channelA = new LoopbackChannel();
        var channelB = new LoopbackChannel();
        channelA.Peer = channelB;
        channelB.Peer = channelA;

        await using var receiver = new TransferSession(channelB, key, destB, null, chunkSize: 4 * 1024);
        await using var sender = new TransferSession(channelA, key, destA, null, chunkSize: 4 * 1024);

        await sender.OpenAsync(WebRtcRole.Offerer);
        await receiver.OpenAsync(WebRtcRole.Answerer);

        var refused = new TaskCompletionSource<TransferRefusal>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Refused += refusal => refused.TrySetResult(refusal);

        try
        {
            await sender.SendFilesAsync([sendPath], "陌生设备");
            Assert(false, "a transfer with nobody to ask should not report success");
        }
        catch (InvalidOperationException)
        {
            Assert(true, "the sender stops when no approval surface exists");
        }

        Assert(await Task.WhenAny(refused.Task, Task.Delay(TimeSpan.FromSeconds(10))) == refused.Task,
            "with no approval handler the transfer is refused");
        Assert(!refused.Task.Result.Retryable,
            "a refusal with nobody to ask is final: asking again would ask the same unanswered question");
        Assert(!File.Exists(Path.Combine(destB, "silent.bin")), "and nothing is written");
    }

    // ---- 16. A channel carrying another group's frames is refused ----
    {
        var destA = Path.Combine(workspace, "wrongkey-a");
        var destB = Path.Combine(workspace, "wrongkey-b");
        Directory.CreateDirectory(destA);
        Directory.CreateDirectory(destB);

        var sendPath = Path.Combine(source, "wrongkey.bin");
        File.WriteAllBytes(sendPath, RandomNumberGenerator.GetBytes(2 * 1024));

        var channelA = new LoopbackChannel();
        var channelB = new LoopbackChannel();
        channelA.Peer = channelB;
        channelB.Peer = channelA;

        // The sender was given a different key than the receiver — the case a shared group id alone
        // must not be enough to pass.
        await using var receiver = new TransferSession(channelB, key, destB, _ => Task.FromResult(true), chunkSize: 2 * 1024);
        await using var sender = new TransferSession(channelA, otherKey, destA, null, chunkSize: 2 * 1024,
            acceptanceWait: TimeSpan.FromSeconds(1));

        await sender.OpenAsync(WebRtcRole.Offerer);
        await receiver.OpenAsync(WebRtcRole.Answerer);

        var refused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.Refused += refusal => refused.TrySetResult(refusal.Message);

        try
        {
            await sender.SendFilesAsync([sendPath], "台式机");
            Assert(false, "a peer that cannot open the frames should not look like a success");
        }
        catch (InvalidOperationException)
        {
            Assert(true, "the sender gives up when the receiver never accepts");
        }

        Assert(await Task.WhenAny(refused.Task, Task.Delay(TimeSpan.FromSeconds(10))) == refused.Task,
            "frames sealed with another key are refused");
        Assert(receiver.IncomingManifest is null, "nothing is adopted from the wrong key");
        Assert(!File.Exists(Path.Combine(destB, "wrongkey.bin")), "and no file appears");
    }

    // ---- 17. A dropped chunk is asked for by index, not by resending the file ----
    {
        var target = Path.Combine(workspace, "chunk-resume");
        Directory.CreateDirectory(target);

        var payload = RandomNumberGenerator.GetBytes(100 * 1024 + 7);
        var path = Path.Combine(source, "断点.bin");
        File.WriteAllBytes(path, payload);

        var sender = new TransferSender(key, [path], "台式机", chunkSize: 16 * 1024, sessionId: "resume-inline");
        using var receiver = new TransferReceiver(key, target, chunkSize: 16 * 1024);

        Assert(receiver.Accept(sender.BuildManifestFrame()).Ok, "the manifest is accepted");
        Assert(!receiver.Resumed, "a first attempt is not a resume");

        var frames = sender.BuildChunkFrames(0).ToList();
        Assert(frames.Count == 7, "the file needs seven chunks at this size");

        // Everything except chunk 2 makes it across, as if that one frame were lost.
        for (var index = 0; index < frames.Count; index++)
            if (index != 2) receiver.Accept(frames[index]);

        var premature = receiver.Accept(sender.BuildCompleteFrame(0));
        Assert(!premature.Ok && premature.Message.Contains("还缺 1", StringComparison.Ordinal),
            "completing with a hole is refused and names the hole");

        var plan = receiver.ResumePlan();
        Assert(plan is not null, "the receiver can state what it needs");
        Assert(plan!.MissingFor(0, frames.Count).SequenceEqual([2]), "the plan lists exactly the missing chunk");
        Assert(!File.Exists(Path.Combine(target, "断点.bin")), "nothing is published while a chunk is missing");

        receiver.Accept(frames[2]);
        var finished = receiver.Accept(sender.BuildCompleteFrame(0));
        Assert(finished.Ok, $"the file completes once the missing chunk arrives: {finished.Message}");
        Assert(File.ReadAllBytes(Path.Combine(target, "断点.bin")).SequenceEqual(payload), "and it is identical");
    }

    // ---- 18. A transfer cut off by a restart resumes from the progress kept on disk ----
    {
        var target = Path.Combine(workspace, "disk-resume");
        Directory.CreateDirectory(target);

        var payload = RandomNumberGenerator.GetBytes(80 * 1024 + 3);
        var path = Path.Combine(source, "重启续传.bin");
        File.WriteAllBytes(path, payload);

        var sender = new TransferSender(key, [path], "台式机", chunkSize: 8 * 1024, sessionId: "resume-across");
        var frames = sender.BuildChunkFrames(0).ToList();
        Assert(frames.Count == 11, "eleven chunks at this size");

        // First process: manifest plus the first four chunks, then it goes away.
        using (var first = new TransferReceiver(key, target, chunkSize: 8 * 1024))
        {
            Assert(first.Accept(sender.BuildManifestFrame()).Ok, "the first attempt accepts the manifest");
            foreach (var frame in frames.Take(4)) first.Accept(frame);
        }

        var sessionDirectory = Path.Combine(target, ".incoming", "resume-across");
        Assert(Directory.Exists(sessionDirectory), "the partial session is kept rather than deleted");
        Assert(!File.Exists(Path.Combine(target, "重启续传.bin")), "no file appears under its real name yet");

        // Second process: same transfer id, so it picks up where the first stopped.
        using var second = new TransferReceiver(key, target, chunkSize: 8 * 1024);
        var resumed = second.Accept(sender.BuildManifestFrame());
        Assert(resumed.Ok, $"the second attempt continues instead of refusing: {resumed.Message}");
        Assert(second.Resumed, "it reports that this is a continuation");
        Assert(second.MissingChunks(0).SequenceEqual(Enumerable.Range(4, 7)),
            "only the chunks that never arrived are missing");

        var plan = second.ResumePlan()!;
        foreach (var chunkIndex in plan.MissingFor(0, frames.Count))
        {
            var frame = sender.BuildChunkFrame(0, chunkIndex);
            Assert(frame is not null, $"chunk {chunkIndex} can be rebuilt on demand");
            Assert(second.Accept(frame!).Ok, $"chunk {chunkIndex} is accepted");
        }

        var completed = second.Accept(sender.BuildCompleteFrame(0));
        Assert(completed.Ok, $"the resumed transfer completes: {completed.Message}");
        Assert(File.ReadAllBytes(Path.Combine(target, "重启续传.bin")).SequenceEqual(payload),
            "the resumed file is byte-for-byte identical");
        Assert(!File.Exists(Path.Combine(sessionDirectory, "0.part")),
            "the partial file is gone once the real file is in place");

        // Asking again after completion is harmless, not a second copy.
        var again = second.Accept(sender.BuildManifestFrame());
        Assert(again.Ok && second.Resumed, "a finished transfer can still be re-announced");
        var repeated = second.Accept(sender.BuildCompleteFrame(0));
        Assert(repeated.Ok && repeated.Message.Contains("已经保存过", StringComparison.Ordinal),
            "and completing it again says it is already saved instead of writing twice");
    }

    // ---- 19. The same transfer id never mixes two different transfers ----
    {
        var target = Path.Combine(workspace, "id-reuse");
        Directory.CreateDirectory(target);

        var firstPath = Path.Combine(source, "甲.bin");
        var secondPath = Path.Combine(source, "乙.bin");
        File.WriteAllBytes(firstPath, RandomNumberGenerator.GetBytes(20 * 1024));
        File.WriteAllBytes(secondPath, RandomNumberGenerator.GetBytes(20 * 1024));

        var first = new TransferSender(key, [firstPath], "台式机", chunkSize: 8 * 1024, sessionId: "shared-id");
        using (var partial = new TransferReceiver(key, target, chunkSize: 8 * 1024))
        {
            partial.Accept(first.BuildManifestFrame());
            partial.Accept(first.BuildChunkFrames(0).First());
        }

        // A different file announced under the same id: the old chunk map must not be adopted.
        var second = new TransferSender(key, [secondPath], "台式机", chunkSize: 8 * 1024, sessionId: "shared-id");
        using var receiver = new TransferReceiver(key, target, chunkSize: 8 * 1024);
        Assert(receiver.Accept(second.BuildManifestFrame()).Ok, "the different manifest is accepted");
        Assert(!receiver.Resumed, "it starts clean instead of inheriting the other file's progress");
        Assert(receiver.MissingChunks(0).Count == (int)Math.Ceiling(20 * 1024 / (double)(8 * 1024)),
            "every chunk of the new file is still missing");
    }

    // ---- 20. Session to session: a resume puts less on the wire than the file itself ----
    {
        var destA = Path.Combine(workspace, "ack-a");
        var destB = Path.Combine(workspace, "ack-b");
        Directory.CreateDirectory(destA);
        Directory.CreateDirectory(destB);

        var payload = RandomNumberGenerator.GetBytes(96 * 1024);
        var path = Path.Combine(source, "会话续传.bin");
        File.WriteAllBytes(path, payload);

        var chunkSize = 8 * 1024;
        var sessionId = "resume-ack";

        // Somebody got four chunks across before the link died.
        var seedSender = new TransferSender(key, [path], "台式机", chunkSize, sessionId);
        using (var seed = new TransferReceiver(key, destB, chunkSize))
        {
            seed.Accept(seedSender.BuildManifestFrame());
            foreach (var frame in seedSender.BuildChunkFrames(0).Take(4)) seed.Accept(frame);
        }

        var channelA = new LoopbackChannel();
        var channelB = new LoopbackChannel();
        channelA.Peer = channelB;
        channelB.Peer = channelA;

        await using var receiver = new TransferSession(channelB, key, destB, _ => Task.FromResult(true), chunkSize);
        await using var sender = new TransferSession(channelA, key, destA, null, chunkSize);
        await sender.OpenAsync(WebRtcRole.Offerer);
        await receiver.OpenAsync(WebRtcRole.Answerer);

        var saved = new TaskCompletionSource<TransferFileProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.FileReceived += progress => saved.TrySetResult(progress);

        var sent = await sender.SendFilesAsync([path], "台式机", CancellationToken.None, sessionId);

        Assert(await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(20))) == saved.Task,
            "the resumed session delivers the file");
        Assert(receiver.Resumed, "the receiving session knows it continued an earlier attempt");
        Assert(sent < payload.Length,
            $"only the missing chunks were sent ({sent} bytes for a {payload.Length} byte file)");
        Assert(File.ReadAllBytes(Path.Combine(destB, "会话续传.bin")).SequenceEqual(payload),
            "the file assembled from both attempts is identical");
    }

    Console.WriteLine($"All {checks} transfer checks passed.");
    return 0;
}
finally
{
    try { Directory.Delete(workspace, true); } catch (IOException) { }
}

static string Flip(string base64)
{
    var bytes = Convert.FromBase64String(base64);
    bytes[^1] ^= 0xFF;
    return Convert.ToBase64String(bytes);
}

/// <summary>Two of these joined by <see cref="Peer"/> behave like an established channel.</summary>
internal sealed class LoopbackChannel : ITransferChannel
{
    public LoopbackChannel? Peer { get; set; }

    public event Action<TransferSignal>? SignalOutbound;

    public event Action? ChannelOpened;

    public event Action<byte[]>? BytesReceived;

    public event Action<string>? Failed;

    public bool IsOpen { get; private set; }

    public Task StartAsync(WebRtcRole role)
    {
        IsOpen = true;
        ChannelOpened?.Invoke();
        return Task.CompletedTask;
    }

    public Task ApplySignalAsync(TransferSignal signal)
    {
        SignalOutbound?.Invoke(signal);
        return Task.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data)
    {
        if (!IsOpen) throw new InvalidOperationException("通道未打开");

        // Delivered on a separate turn so the two sessions do not recurse into each other.
        var peer = Peer;
        if (peer is not null)
        {
            await Task.Yield();
            peer.BytesReceived?.Invoke(data.ToArray());
        }
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        Failed?.Invoke("closed");
        return ValueTask.CompletedTask;
    }
}


