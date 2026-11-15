/**
 * 仙灵 Fairy Code 审批扩展 (for Pi Agent / @earendil-works/pi-coding-agent)
 *
 * 这个扩展没有任何判断权。它只做一件事：在每次工具调用真正执行之前，把调用原样发给
 * 仙灵本机的审批桥接，并把"能不能执行"完全交给桥接背后的审批闸门。
 *
 * 三条硬规则：
 * 1. 环境变量缺失 → 全部拦截（fail closed），绝不因为桥接连不上就放行。
 * 2. 桥接超时 / 报错 / 返回体无法解析 → 拦截。
 * 3. 只有桥接明确回答 allow: true 才放行；其余一切情况都是拦截。
 *
 * 桥接令牌只能证明"这个进程可以提问"，不能批准任何动作：批准永远来自用户在控制
 * 中心里对一条待审批项的答复。
 */

export default function (pi) {
  const bridgeUrl = process.env.FAIRY_BRIDGE_URL || "";
  const token = process.env.FAIRY_BRIDGE_TOKEN || "";
  const taskId = process.env.FAIRY_TASK_ID || "";
  const configured = bridgeUrl.length > 0 && token.length > 0 && taskId.length > 0;

  // 用户配置的是非内置的服务商时，用显式的 OpenAI 兼容端点注册，绝不猜地址。
  const customBaseUrl = process.env.FAIRY_CUSTOM_BASE_URL || "";
  if (customBaseUrl.length > 0) {
    const modelId = process.env.FAIRY_CUSTOM_MODEL || "default";
    pi.registerProvider("fairy-endpoint", {
      baseUrl: customBaseUrl,
      apiKey: process.env.FAIRY_CUSTOM_KEY ? "$FAIRY_CUSTOM_KEY" : "not-required",
      api: "openai-completions",
      models: [
        {
          id: modelId,
          name: modelId,
          reasoning: false,
          input: ["text"],
          cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
          contextWindow: 128000,
          maxTokens: 8192,
        },
      ],
    });
  }

  if (!configured) {
    console.error("[fairy] 审批桥接未配置，所有工具调用将被拦截");
  }

  function block(reason) {
    return { block: true, reason: `仙灵审批：${reason}` };
  }

  pi.on("tool_call", async (event, ctx) => {
    if (!configured) return block("审批桥接未配置");

    let response;
    try {
      response = await fetch(`${bridgeUrl}decide`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Fairy-Bridge": token,
        },
        body: JSON.stringify({
          taskId,
          toolName: event.toolName,
          input: event.input ?? {},
        }),
        signal: AbortSignal.timeout(330000),
      });
    } catch (error) {
      return block(`审批桥接不可达（${String(error && error.name ? error.name : error)}）`);
    }

    if (!response.ok) {
      return block(`审批桥接返回 HTTP ${response.status}`);
    }

    let payload;
    try {
      payload = await response.json();
    } catch (error) {
      return block("审批桥接返回内容无法解析");
    }

    if (payload && payload.allow === true) return undefined;

    const reason = payload && typeof payload.reason === "string" && payload.reason.length > 0
      ? payload.reason
      : "未获得批准";
    return block(reason);
  });
}
