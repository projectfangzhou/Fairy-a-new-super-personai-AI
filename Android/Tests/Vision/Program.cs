using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FairyAI_Android.Services.Knowledge;
using FairyAI_Android.Services.Tools;
using FairyAI_Android.Services.Vision;

var checks = 0;

void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}

// A 1x1 PNG header is enough: the code only looks at the magic bytes.
var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };

// ------------------------------------------------------------------ endpoint choice

var visionSettings = new VisionSettings("MiMo", "https://example.test/v1/chat/completions", "mimo-vl", "k1");
var customSettings = new MultimodalSettings(true, "https://custom.test/v1/chat/completions", "custom-vl", "k2");

var (target, reason) = VisionTargetSelector.Select(visionSettings, customSettings);
Assert(target is not null && target.Name == "MiMo", "a configured vision model is used first");
Assert(target!.Model == "mimo-vl" && target.BaseUrl == "https://example.test/v1/chat/completions",
    "the target carries the configured endpoint and model");
Assert(reason.Length == 0, "a usable target has no complaint");

var (fallback, _) = VisionTargetSelector.Select(new VisionSettings("OpenAI", "", "", ""), customSettings);
Assert(fallback is not null && fallback.Name == "自定义多模态", "an empty vision block falls back to custom multimodal");

var (missing, missingReason) = VisionTargetSelector.Select(new VisionSettings("OpenAI", "", "", ""), null);
Assert(missing is null, "no endpoint at all yields no target");
Assert(missingReason == VisionTargetSelector.MissingReason, "the missing-endpoint reason is the documented one");
Assert(missingReason.Contains("视觉模型", StringComparison.Ordinal), "the reason says what to configure");

var (disabledCustom, _) = VisionTargetSelector.Select(
    new VisionSettings("OpenAI", "  ", "  ", ""), new MultimodalSettings(false, "https://x.test", "m", "k"));
Assert(disabledCustom is null, "a disabled custom platform is not used even when filled in");

var (providerless, _) = VisionTargetSelector.Select(new VisionSettings("", "https://a.test", "m", ""), null);
Assert(providerless is not null && providerless.Name == "视觉模型", "a target without a provider name still reports one");

// ------------------------------------------------------------------ the main model as a fallback

var mainModel = new VisionSettings("Kimi", "https://api.moonshot.cn/v1/chat/completions", "kimi-k2.6", "k")
{
    SeesImages = true,
};

var (noVisionWithMain, _) = VisionTargetSelector.Select(
    new VisionSettings("OpenAI", "", "", ""), null, mainModel);
Assert(noVisionWithMain is not null && noVisionWithMain.Name.Contains("主模型", StringComparison.Ordinal),
    "a main model proven to see images is used when no vision block is configured");
Assert(noVisionWithMain!.Model == "kimi-k2.6", "the fallback carries the main model's endpoint");

var (unprovenMain, unprovenReason) = VisionTargetSelector.Select(
    new VisionSettings("OpenAI", "", "", ""), null,
    mainModel with { SeesImages = false });
Assert(unprovenMain is null, "a main model that was never probed is not used for vision");
Assert(unprovenReason == VisionTargetSelector.MissingReason, "the reason explains how to enable it");
Assert(unprovenReason.Contains("测一下", StringComparison.Ordinal), "the reason mentions the probe");

var (visionBeatsMain, _) = VisionTargetSelector.Select(visionSettings, null, mainModel);
Assert(visionBeatsMain!.Name == "MiMo", "an explicitly configured vision block still wins over the main model");

// ------------------------------------------------------------------ the probe

var sample = VisionProbe.SampleImage();
Assert(sample.Length > 40, "the probe ships a real image");
Assert(VisionRequest.MimeOf(sample) == "image/png", "the probe image is a PNG");
Assert(VisionProbe.Prompt.Contains("颜色", StringComparison.Ordinal), "the probe asks about the colour");

var sees = VisionProbe.Classify(200, """{"choices":[{"message":{"content":"红色"}}]}""");
Assert(sees.CanSeeImages && sees.Conclusive, "a correct colour answer proves the model can see");
Assert(sees.Message.Contains("红色", StringComparison.Ordinal), "the answer is reported back");

var seesEnglish = VisionProbe.Classify(200, """{"choices":[{"message":{"content":"It is Red."}}]}""");
Assert(seesEnglish.CanSeeImages, "an English colour answer counts as well");

var blindGuess = VisionProbe.Classify(200, """{"choices":[{"message":{"content":"我无法查看图片。"}}]}""");
Assert(!blindGuess.CanSeeImages && blindGuess.Conclusive, "an answer that does not name the colour is a conclusive no");
Assert(blindGuess.Message.Contains("没认出", StringComparison.Ordinal), "it says the answer missed the colour");

var refusesImage = VisionProbe.Classify(400, """{"error":{"message":"content must be text"}}""");
Assert(!refusesImage.CanSeeImages && refusesImage.Conclusive, "a 400 is a conclusive refusal");
Assert(refusesImage.Message.Contains("不接受图片", StringComparison.Ordinal), "a 400 is reported as an endpoint that cannot take images");

var unauthorizedProbe = VisionProbe.Classify(401, """{"error":{"message":"invalid key"}}""");
Assert(!unauthorizedProbe.CanSeeImages, "a 401 is not a pass");
Assert(!unauthorizedProbe.Conclusive, "a 401 says nothing about whether the model can see");
Assert(unauthorizedProbe.Message.Contains("401", StringComparison.Ordinal), "the status is reported");

var serverError = VisionProbe.Classify(503, "");
Assert(!serverError.Conclusive, "a server error is not a capability verdict");

var probeHandler = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"红色"}}]}""");
var probeTarget = new VisionTarget(mainModel.Provider, mainModel.BaseUrl, mainModel.Model, mainModel.ApiKey);
using (var client = new VisionClient(() => probeTarget, new HttpClient(probeHandler)))
{
    var probe = await client.ProbeAsync();
    Assert(probe.CanSeeImages, "the client can probe an endpoint end to end");
    Assert(probeHandler.LastBody!.Contains("data:image/png;base64,", StringComparison.Ordinal),
        "the probe really sends an image, not just a question");
    Assert(probeHandler.LastAuthorization == "Bearer k", "the probe uses the endpoint's key");
}

var probeWithoutTarget = new StubHandler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"红色"}}]}""");
using (var client = new VisionClient(() => null, new HttpClient(probeWithoutTarget)))
{
    var probe = await client.ProbeAsync();
    Assert(!probe.CanSeeImages && !probe.Conclusive && probe.Message == VisionTargetSelector.MissingReason,
        "probing without an endpoint is inconclusive and says what is missing");
    Assert(probeWithoutTarget.Calls == 0, "probing without an endpoint sends nothing");
}

using (var client = new VisionClient(() => probeTarget, new HttpClient(new ThrowingHandler())))
{
    var probe = await client.ProbeAsync();
    Assert(!probe.Conclusive, "a network failure is not recorded as 'cannot see images'");
    Assert(probe.Message.Contains("连不上", StringComparison.Ordinal), "the network failure is reported as such");
}

// ------------------------------------------------------------------ request shape

var body = VisionRequest.BuildBody(target, "看一下", pngBytes);
using (var document = JsonDocument.Parse(body))
{
    var root = document.RootElement;
    Assert(root.GetProperty("model").GetString() == "mimo-vl", "the request carries the model");
    Assert(root.GetProperty("max_tokens").GetInt32() == VisionRequest.DefaultMaxTokens, "a token budget is sent");

    var content = root.GetProperty("messages")[0].GetProperty("content");
    Assert(root.GetProperty("messages")[0].GetProperty("role").GetString() == "user", "the message is from the user");
    Assert(content[0].GetProperty("type").GetString() == "text", "the prompt is a text part");
    Assert(content[0].GetProperty("text").GetString() == "看一下", "the prompt text is what the caller asked");

    var url = content[1].GetProperty("image_url").GetProperty("url").GetString()!;
    Assert(url.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "a PNG is declared as PNG");
    Assert(url.Length > 30, "the image travels as base64 data, not as a path");
}

Assert(VisionRequest.MimeOf(jpegBytes) == "image/jpeg", "a JPEG is declared as JPEG");
Assert(VisionRequest.MimeOf(new byte[] { 1, 2, 3 }) == "image/png", "unknown bytes fall back to PNG instead of crashing");
Assert(VisionRequest.MimeOf(Array.Empty<byte>()) == "image/png", "an empty image does not throw");

var defaultPrompt = VisionRequest.BuildPrompt(null);
Assert(defaultPrompt.Contains("不要猜", StringComparison.Ordinal), "the default prompt forbids guessing");
Assert(defaultPrompt.Contains("简要说明", StringComparison.Ordinal), "the default prompt asks for a description");

var questionPrompt = VisionRequest.BuildPrompt("  这个页面在报什么错？ ");
Assert(questionPrompt.Contains("这个页面在报什么错？", StringComparison.Ordinal), "the user's question reaches the prompt");
Assert(questionPrompt.Contains("不要猜", StringComparison.Ordinal), "the instructions survive a user question");

// ------------------------------------------------------------------ reply parsing

const string goodReply = """
{"choices":[{"message":{"role":"assistant","content":"  这是一个设置页面。  "}}]}
""";
var (ok, text, error) = VisionReply.Parse(200, goodReply);
Assert(ok && text == "这是一个设置页面。", "a normal reply is parsed and trimmed");
Assert(error is null, "a good reply has no error");

const string arrayReply = """
{"choices":[{"message":{"content":[{"type":"text","text":"第一段"},{"type":"text","text":"第二段"}]}}]}
""";
var (arrayOk, arrayText, _) = VisionReply.Parse(200, arrayReply);
Assert(arrayOk && arrayText == "第一段第二段", "array-shaped content is joined instead of being dropped");

var (emptyOk, _, emptyError) = VisionReply.Parse(200, """{"choices":[{"message":{"content":""}}]}""");
Assert(!emptyOk && emptyError!.Contains("空内容", StringComparison.Ordinal), "an empty answer is reported, not returned as success");

var (noChoices, _, noChoicesError) = VisionReply.Parse(200, """{"id":"x"}""");
Assert(!noChoices && noChoicesError!.Contains("没有 choices", StringComparison.Ordinal), "a reply without choices is an error");

var (apiError, _, apiErrorMessage) = VisionReply.Parse(200, """{"error":{"message":"model not found"}}""");
Assert(!apiError && apiErrorMessage!.Contains("model not found", StringComparison.Ordinal), "an API-level error is surfaced");

var (unauthorized, _, unauthorizedError) = VisionReply.Parse(401, """{"error":{"message":"invalid key"}}""");
Assert(!unauthorized && unauthorizedError!.StartsWith("视觉接口返回 HTTP 401", StringComparison.Ordinal),
    "an HTTP error names the status");

var (html, _, htmlError) = VisionReply.Parse(200, "<html>proxy error</html>");
Assert(!html && htmlError!.Contains("不是 JSON", StringComparison.Ordinal), "a non-JSON body is reported as such");

var (emptyBody, _, emptyBodyError) = VisionReply.Parse(200, "");
Assert(!emptyBody && emptyBodyError!.Contains("空响应", StringComparison.Ordinal), "an empty response is an error");

// ------------------------------------------------------------------ the client

var handler = new StubHandler(HttpStatusCode.OK, goodReply);
using (var client = new VisionClient(() => target, new HttpClient(handler)))
{
    var answer = await client.DescribeAsync(pngBytes, "这上面写了什么");
    Assert(answer.Ok && answer.Text == "这是一个设置页面。", "the client returns the model's text");
    Assert(answer.ImageBytes == pngBytes.Length, "the answer records how many bytes were sent");
    Assert(client.LastTargetName == "MiMo", "the client remembers which endpoint answered");

    Assert(handler.LastRequest?.Method == HttpMethod.Post, "the request is a POST");
    Assert(handler.LastRequest?.RequestUri?.ToString() == target.BaseUrl, "the request goes to the configured URL");
    Assert(handler.LastAuthorization == "Bearer k1", "the key travels as a bearer token");
    Assert(handler.LastBody!.Contains("data:image/png;base64,", StringComparison.Ordinal), "the body carries the image");
}

var noKeyHandler = new StubHandler(HttpStatusCode.OK, goodReply);
using (var client = new VisionClient(() => target with { ApiKey = "  " }, new HttpClient(noKeyHandler)))
{
    await client.DescribeAsync(pngBytes, null);
    Assert(noKeyHandler.LastAuthorization is null, "no key means no authorization header, not an empty one");
}

var noTargetHandler = new StubHandler(HttpStatusCode.OK, goodReply);
using (var client = new VisionClient(() => null, new HttpClient(noTargetHandler)))
{
    var answer = await client.DescribeAsync(pngBytes, null);
    Assert(!answer.Ok && answer.Error == VisionTargetSelector.MissingReason, "an unconfigured model is reported before calling");
    Assert(noTargetHandler.Calls == 0, "an unconfigured model sends no request at all");
}

using (var client = new VisionClient(() => target, new HttpClient(noTargetHandler)))
{
    var answer = await client.DescribeAsync(Array.Empty<byte>(), null);
    Assert(!answer.Ok && answer.Error!.Contains("没有画面", StringComparison.Ordinal), "no image is refused before calling");
    Assert(noTargetHandler.Calls == 0, "no image sends no request");
}

var failingHandler = new StubHandler(HttpStatusCode.Forbidden, """{"error":{"message":"blocked"}}""");
using (var client = new VisionClient(() => target, new HttpClient(failingHandler)))
{
    var answer = await client.DescribeAsync(pngBytes, null);
    Assert(!answer.Ok && answer.Error!.Contains("HTTP 403", StringComparison.Ordinal), "a 403 becomes a readable error");
    Assert(answer.ImageBytes == pngBytes.Length, "even a failure records the image size");
}

var throwingHandler = new ThrowingHandler();
using (var client = new VisionClient(() => target, new HttpClient(throwingHandler)))
{
    var answer = await client.DescribeAsync(pngBytes, null);
    Assert(!answer.Ok && answer.Error!.Contains("连不上视觉接口", StringComparison.Ordinal),
        "a network failure is reported instead of thrown");
}

// ------------------------------------------------------------------ important info

var details = ImportantInfoExtractor.Extract("明天下午三点在三楼会议室，电话 13800138000，地址 北京市海淀区中关村大街1号。");
Assert(details.Any(item => item.StartsWith("日期: 明天", StringComparison.Ordinal)), "a Chinese date word is extracted");
Assert(details.Any(item => item == "电话: 13800138000"), "a mobile number is extracted");
Assert(details.Any(item => item.StartsWith("地址: ", StringComparison.Ordinal)), "an address is extracted");
Assert(details.Count == details.Distinct().Count(), "the same detail is not listed twice");

Assert(ImportantInfoExtractor.Extract("这段文字里没有任何可提取的内容。").Count == 0, "plain text yields nothing");
Assert(ImportantInfoExtractor.Extract(null).Count == 0, "null text does not throw");

var repeated = ImportantInfoExtractor.Extract("2026-11-15 和 2026-11-15 都是同一天");
Assert(repeated.Count == 1, "an identical date is only listed once");

// ------------------------------------------------------------------ the tool

var successTool = new DescribeScreenTool(_ => Task.FromResult(
    new VisionOutcome(true, "看了这一屏（全屏截图 900 KB）：\n桌面上有四个图标。", "桌面上有四个图标。", "全屏截图 900 KB",
        Array.Empty<string>())));

var success = await successTool.ExecuteAsync(new JsonObject { ["question"] = "这是哪个应用" });
Assert(success.Contains("看了这一屏", StringComparison.Ordinal), "the tool returns the description");
Assert(success.Contains("四个图标", StringComparison.Ordinal), "the model's text reaches the tool result");

string? seenQuestion = "unset";
var refusingTool = new DescribeScreenTool(question =>
{
    seenQuestion = question;
    return Task.FromResult(VisionOutcome.Fail("手机屏幕操作总开关是关的",
        "手机屏幕操作总开关是关的：在「看板 → 设备连接」里打开后再试。"));
});

var refused = await refusingTool.ExecuteAsync(new JsonObject { ["question"] = "看看有没有报错" });
Assert(refused.Contains("总开关是关的", StringComparison.Ordinal), "a refusal is passed through verbatim");
Assert(seenQuestion == "看看没有报错".Replace("没有", "有没有"), "the question reaches the capture step");

var bare = await refusingTool.ExecuteAsync(new JsonObject());
Assert(seenQuestion is null, "an absent question stays absent instead of becoming an empty string");
Assert(bare.Length > 0, "a tool result is never empty");

var toolSchema = JsonSerializer.Serialize(ToolSchema.BuildDefinition(successTool));
using (var document = JsonDocument.Parse(toolSchema))
{
    var function = document.RootElement.GetProperty("function");
    Assert(function.GetProperty("name").GetString() == "describe_screen", "the tool is announced by name");
    Assert(function.GetProperty("parameters").GetProperty("required").GetArrayLength() == 0,
        "the question parameter is optional");
}

Console.WriteLine();
Console.WriteLine($"{checks} checks passed");

// ------------------------------------------------------------------ fakes

sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    public int Calls { get; private set; }

    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastBody { get; private set; }

    public string? LastAuthorization { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;
        LastAuthorization = request.Headers.Authorization?.ToString();
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
    }
}

sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("无名主机");
}
