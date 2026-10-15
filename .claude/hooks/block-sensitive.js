// PreToolUse hook: deny any tool call that touches secrets/, *.pem or *.db.
// Reads the hook payload (JSON) from stdin and prints a "deny" decision when a
// file path or shell command references one of the protected locations.

const RULES = [
  { label: "secrets/ directory", re: /(^|[\s"'=:\/\\])secrets(?=[\/\\\s"']|$)/i },
  { label: "*.pem key file", re: /\.pem\b/i },
  { label: "*.db database file", re: /\.db\b/i },
];

let raw = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => (raw += chunk));
process.stdin.on("end", () => {
  const input = JSON.parse(raw);
  const toolInput = input.tool_input ?? {};

  // File tools pass a path; shell tools pass a command line.
  const targets = [toolInput.file_path, toolInput.path, toolInput.command]
    .filter((v) => typeof v === "string");

  for (const target of targets) {
    const hit = RULES.find((r) => r.re.test(target));
    if (hit) {
      process.stdout.write(JSON.stringify({
        hookSpecificOutput: {
          hookEventName: "PreToolUse",
          permissionDecision: "deny",
          permissionDecisionReason:
            `Blocked by .claude/hooks/block-sensitive.js: ${input.tool_name} touches a ${hit.label}. ` +
            "These files must not be read, edited or printed (see CLAUDE.md). Reference the path only.",
        },
      }));
      return;
    }
  }
});
