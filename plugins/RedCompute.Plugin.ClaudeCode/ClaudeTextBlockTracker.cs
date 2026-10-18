using System.Text;

namespace RedCompute.Plugin.ClaudeCode;

/// <summary>
/// Gives Claude assistant text the same commentary/final contract Codex declares natively.
///
/// Claude's stream protocol never labels text. With <c>--include-partial-messages</c> the CLI streams
/// <c>text_delta</c>s, then emits one <c>assistant</c> message per completed block with a null
/// <c>stop_reason</c>; the API message's real stop reason only arrives on <c>message_delta</c>. A text
/// block's role is therefore unknown while it streams and becomes known later, deterministically:
/// <list type="bullet">
/// <item>a subsequent tool_use block (or <c>stop_reason: tool_use</c>) means the model kept working, so
/// the text was <c>commentary</c>;</item>
/// <item>text still unsettled when the turn's <c>result</c> arrives (or the process ends) is the
/// turn's <c>final_answer</c>.</item>
/// </list>
/// Each text block is accumulated here and persisted exactly once, after its phase is decided, so
/// the durable phase can never change after a consumer (Discord) has delivered it. Thinking blocks
/// use the same API-message/thread identity map and are persisted once at <c>content_block_stop</c>
/// (or the turn's abnormal end). Live deltas carry no phase: undetermined, never contradicting the
/// record.
/// </summary>
internal sealed class ClaudeTextBlockTracker
{
    internal const string Commentary = "commentary";
    internal const string FinalAnswer = "final_answer";

    private readonly object _gate = new();
    private readonly List<Block> _pending = [];
    private readonly List<Block> _pendingThinking = [];
    private readonly Dictionary<string, string> _apiMessageIds = new(StringComparer.Ordinal);
    private int _anonymousMessages;

    internal sealed record SettledText(string MessageId, string? MessageUid, string Content, string Phase);
    internal sealed record SettledThinking(string MessageId, string? MessageUid, string Content);

    private sealed class Block(string thread, string messageId, int index, string? messageUid)
    {
        public string Thread { get; } = thread;
        public string MessageId { get; } = messageId;
        public int Index { get; } = index;
        public string? MessageUid { get; } = messageUid;
        public StringBuilder Content { get; } = new();
    }

    private static string Thread(string? parentToolUseId) => parentToolUseId ?? "";

    /// <summary><c>message_start</c>: every later block index belongs to this API message.</summary>
    public void BeginMessage(string? parentToolUseId, string? apiMessageId)
    {
        lock (_gate)
        {
            _apiMessageIds[Thread(parentToolUseId)] = string.IsNullOrEmpty(apiMessageId)
                ? $"claude-message-{++_anonymousMessages}"
                : apiMessageId;
        }
    }

    /// <summary>
    /// Appends one streamed text delta and returns the block's stable identity, which the live
    /// delta and the eventual durable record share.
    /// </summary>
    public string Append(string? parentToolUseId, int index, string? text, string? messageUid)
        => Append(_pending, parentToolUseId, index, text, messageUid);

    /// <summary>
    /// Appends one streamed thinking delta and returns the stable identity shared by its live
    /// deltas and eventual durable record.
    /// </summary>
    public string AppendThinking(string? parentToolUseId, int index, string? thinking, string? messageUid)
        => Append(_pendingThinking, parentToolUseId, index, thinking, messageUid);

    private string Append(List<Block> pending, string? parentToolUseId, int index, string? content,
        string? messageUid)
    {
        lock (_gate)
        {
            var thread = Thread(parentToolUseId);
            if (!_apiMessageIds.TryGetValue(thread, out var apiMessageId))
                _apiMessageIds[thread] = apiMessageId = $"claude-message-{++_anonymousMessages}";
            var block = pending.LastOrDefault(b =>
                b.Thread == thread && b.MessageId == apiMessageId && b.Index == index);
            if (block is null)
            {
                block = new Block(thread, apiMessageId, index, messageUid);
                pending.Add(block);
            }
            block.Content.Append(content);
            return BlockId(block);
        }
    }

    /// <summary>
    /// The model continued working (a tool_use block started, or the API message stopped for
    /// tool use): everything it wrote before that on this thread was commentary.
    /// </summary>
    public IReadOnlyList<SettledText> SettleCommentary(string? parentToolUseId)
    {
        var thread = Thread(parentToolUseId);
        return Take(block => block.Thread == thread, _ => Commentary);
    }

    /// <summary>
    /// The turn ended (result, process exit, stop). Unsettled main-thread text is the turn's
    /// answer; subagent text is never the parent turn's answer.
    /// </summary>
    public IReadOnlyList<SettledText> SettleTurn()
        => Take(_ => true, block => block.Thread.Length == 0 ? FinalAnswer : Commentary);

    /// <summary><c>content_block_stop</c>: persist this complete thinking block once.</summary>
    public IReadOnlyList<SettledThinking> SettleThinking(string? parentToolUseId, int index)
        => TakeThinking(block => block.Thread == Thread(parentToolUseId) && block.Index == index);

    /// <summary>Abnormal turn end: preserve every thinking block that was already shown live.</summary>
    public IReadOnlyList<SettledThinking> SettleTurnThinking()
        => TakeThinking(_ => true);

    public bool HasPending
    {
        get { lock (_gate) return _pending.Count > 0 || _pendingThinking.Count > 0; }
    }

    private IReadOnlyList<SettledText> Take(Func<Block, bool> selector, Func<Block, string> phase)
    {
        lock (_gate)
        {
            var taken = _pending.Where(selector).ToList();
            if (taken.Count == 0) return [];
            _pending.RemoveAll(taken.Contains);
            return taken
                .Select(block => (Block: block, Content: block.Content.ToString()))
                // Empty text maps to no display record, exactly as Codex drops empty agent text.
                .Where(item => !string.IsNullOrWhiteSpace(item.Content))
                .Select(item => new SettledText(BlockId(item.Block), item.Block.MessageUid, item.Content, phase(item.Block)))
                .ToList();
        }
    }

    private IReadOnlyList<SettledThinking> TakeThinking(Func<Block, bool> selector)
    {
        lock (_gate)
        {
            var taken = _pendingThinking.Where(selector).ToList();
            if (taken.Count == 0) return [];
            _pendingThinking.RemoveAll(taken.Contains);
            return taken
                .Select(block => (Block: block, Content: block.Content.ToString()))
                .Where(item => !string.IsNullOrWhiteSpace(item.Content))
                .Select(item => new SettledThinking(
                    BlockId(item.Block), item.Block.MessageUid, item.Content))
                .ToList();
        }
    }

    private static string BlockId(Block block) => $"{block.MessageId}:{block.Index}";
}
