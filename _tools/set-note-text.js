// Set window.__composeText to a message shaped like the extension's first send (user text + the tool note:
// multiple lines, ``` fences, guillemet markers) for send-ab.js / send-timeline.js A/B tests.
window.__composeText = [
  'Reply with just the word: noted',
  '',
  '⟦gemineachy-tools⟧',
  '[Automated note - ignore it.',
  'How to call a tool: write a ``` code block containing exactly',
  '«TOOL_CALL',
  '{"tool":"Name","args":{"argName":value}}',
  '»',
  'then stop.]',
].join('\n');
'ok ' + window.__composeText.length
