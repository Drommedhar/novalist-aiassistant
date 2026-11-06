const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');

function page() {
  class Element {
    constructor() {
      this.children = [];
      this.events = new Map();
      this.value = '';
      this.hidden = false;
      this.disabled = false;
      this.checked = false;
      this._text = '';
      this.style = {};
      this.attributes = new Map();
      this.popoverOpen = false;
    }
    set textContent(value) { this._text = value; this.children = []; }
    get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
    get childElementCount() { return this.children.length; }
    appendChild(child) { child.parent = this; this.children.push(child); }
    remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); }
    addEventListener(name, handler) { this.events.set(name, handler); }
    setAttribute(name, value) { this.attributes.set(name, value); }
    reportValidity() { return true; }
    getBoundingClientRect() { return { left: 10, top: 700, bottom: 750 }; }
    get offsetHeight() { return Math.min(200, Number.parseFloat(this.style.maxHeight) || 200); }
    showPopover() {
      this.popoverOpen = true;
      this.events.get('toggle')?.({ newState: 'open' });
    }
    hidePopover() {
      if (!this.popoverOpen) return;
      this.popoverOpen = false;
      this.events.get('toggle')?.({ newState: 'closed' });
    }
    querySelectorAll(selector) {
      const descendants = this.children.flatMap(child => [child, ...child.querySelectorAll('*')]);
      if (selector === 'input:checked') return descendants.filter(child => child.type === 'checkbox' && child.checked);
      if (selector === 'input[type="checkbox"]') return descendants.filter(child => child.type === 'checkbox');
      return descendants;
    }
  }
  const elements = new Map();
  const element = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  element('context').hidden = true;
  element('previewPanel').hidden = true;
  element('context').appendChild(element('contextOptions'));
  const posts = [];
  const frames = new Map();
  let frameId = 0;
  let receive;
  const context = vm.createContext({
    document: {
      documentElement: { clientWidth: 1200, clientHeight: 800 },
      getElementById: element,
      querySelectorAll: () => [],
      createElement: () => new Element(),
      createTextNode: text => { const node = new Element(); node.textContent = text; return node; },
    },
    parent: { postMessage: message => posts.push(JSON.parse(message.novalistExt)) },
    window: { addEventListener: (_, handler) => { receive = handler; } },
    requestAnimationFrame: callback => { frames.set(++frameId, callback); return frameId; },
    cancelAnimationFrame: id => frames.delete(id),
  });
  const html = readFileSync(join(__dirname, '../web/chat.html'), 'utf8');
  vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1], context);
  posts.length = 0;
  return {
    element, posts,
    send: () => vm.runInContext('send()', context),
    message: message => receive({ data: { novalistHost: JSON.stringify(message) } }),
    flush: () => { for (const callback of frames.values()) callback(); frames.clear(); },
  };
}

test('pending and active sends preserve drafts and ignore a second Enter', () => {
  const chat = page();
  chat.element('input').value = 'first';
  chat.send();
  chat.send();
  assert.equal(chat.posts.length, 1);
  assert.equal(chat.element('input').value, 'first');
  assert.equal(chat.element('send').disabled, true);
  chat.message({ type: 'state', isGenerating: true });
  chat.message({ type: 'message', role: 'user', text: 'first' });
  assert.equal(chat.element('input').value, '');
  chat.element('input').value = 'next draft';
  chat.send();
  assert.equal(chat.posts.length, 1);
  assert.equal(chat.element('input').value, 'next draft');
  chat.message({ type: 'state', isGenerating: false });
  chat.send();
  assert.equal(chat.posts[1].text, 'next draft');
});

test('rejected or cancelled context reads allow retry without losing the draft', () => {
  const chat = page();
  chat.element('input').value = 'draft';
  chat.send();
  chat.message({ type: 'sendRejected', text: 'draft', error: 'Context is too large' });
  assert.equal(chat.element('send').disabled, false);
  assert.equal(chat.element('input').value, 'draft');
  chat.send();
  chat.element('cancel').events.get('click')();
  assert.equal(chat.element('send').disabled, false);
  assert.equal(chat.element('input').value, 'draft');
});

test('conversation reset discards old stream, preview, context and pending draft', () => {
  const chat = page();
  chat.element('previewPanel').textContent = 'old private preview';
  chat.element('previewPanel').hidden = false;
  chat.element('contextOptions').textContent = 'old context';
  chat.element('input').value = 'old draft';
  chat.message({ type: 'state', isGenerating: true, streamingDelta: 'old stream' });
  chat.message({ type: 'history', reset: true, messages: [] });
  chat.flush();
  assert.equal(chat.element('messages').textContent, '');
  assert.equal(chat.element('previewPanel').textContent, '');
  assert.equal(chat.element('previewPanel').hidden, true);
  assert.equal(chat.element('contextOptions').textContent, '');
  assert.equal(chat.element('input').value, '');
  chat.message({ type: 'state', isGenerating: true, streamingDelta: 'new stream' });
  chat.flush();
  assert.equal(chat.element('messages').textContent, 'new stream');
});

test('hydration restores an active stream and terminal messages do not duplicate it', () => {
  const chat = page();
  chat.element('input').value = 'typed before hydration';
  chat.message({ type: 'history', messages: [], isGenerating: true, streamingResponse: 'existing' });
  chat.flush();
  assert.equal(chat.element('messages').textContent, 'existing');
  assert.equal(chat.element('input').value, 'typed before hydration');
  chat.message({ type: 'state', isGenerating: true, streamingDelta: ' delta' });
  chat.flush();
  assert.equal(chat.element('messages').textContent, 'existing delta');
  chat.message({ type: 'message', role: 'assistant', text: 'existing delta' });
  chat.message({ type: 'state', isGenerating: false, reset: true });
  chat.flush();
  assert.equal(chat.element('messages').textContent, 'existing delta');
  assert.equal(chat.element('send').disabled, false);
});

test('reopening context refreshes scene options and keeps available selections for preview and send', () => {
  const chat = page();
  const toggle = () => chat.element('contextToggle').events.get('click')();
  const options = scene => ({ type: 'contextOptions', groups: [{ title: 'Story', items: [
    { id: 'scene:' + scene, label: scene }, { id: 'outline', label: 'Outline' },
  ] }] });
  toggle();
  chat.message(options('one'));
  for (const box of chat.element('context').querySelectorAll('input[type="checkbox"]')) {
    box.checked = true;
    box.events.get('change')();
  }
  toggle();
  toggle();
  assert.equal(chat.posts.filter(post => post.type === 'contextOptions').length, 2);
  chat.message(options('two'));
  assert.equal(chat.element('contextCount').textContent, '1');
  chat.element('input').value = 'draft';
  chat.element('previewPanel').hidden = true;
  chat.element('preview').onclick();
  assert.deepEqual(chat.posts.at(-1).include, ['outline']);
  chat.send();
  assert.deepEqual(chat.posts.at(-1).include, ['outline']);
});

test('context refresh with no options clears selected count', () => {
  const chat = page();
  chat.message({ type: 'contextOptions', groups: [{ title: 'Story', items: [{ id: 'outline', label: 'Outline' }] }] });
  const box = chat.element('context').querySelectorAll('input[type="checkbox"]')[0];
  box.checked = true;
  box.events.get('change')();
  assert.equal(chat.element('contextCount').textContent, '1');
  chat.message({ type: 'contextOptions', groups: [] });
  assert.equal(chat.element('contextCount').hidden, true);
});

test('Add all selects every context option for preview and send and survives refresh', () => {
  const chat = page();
  const options = { type: 'contextOptions', groups: [
    { title: 'Story', items: [{ id: 'scene:one', label: 'One' }, { id: 'outline', label: 'Outline' }] },
    { title: 'Lore', items: [{ id: 'entity:myth', label: 'Myth' }] },
  ] };
  chat.message(options);
  const button = chat.element('contextOptions').children.find(child => child.type === 'button');
  assert.ok(button, 'Add all button exists');
  button.events.get('click')();
  assert.equal(chat.element('contextCount').textContent, '3');
  chat.message(options);
  chat.element('input').value = 'Hello';
  chat.element('previewPanel').hidden = true;
  chat.element('preview').onclick();
  assert.deepEqual(chat.posts.at(-1).include, ['scene:one', 'outline', 'entity:myth']);
  chat.send();
  assert.deepEqual(chat.posts.at(-1).include, ['scene:one', 'outline', 'entity:myth']);
});

test('Context popover dismissal preserves selection and updates expanded state', () => {
  const chat = page();
  chat.element('contextToggle').events.get('click')();
  chat.message({ type: 'contextOptions', groups: [{ title: 'Lore', items: [{ id: 'entity:myth', label: 'Myth' }] }] });
  const panel = chat.element('context');
  panel.querySelectorAll('input[type="checkbox"]')[0].checked = true;
  assert.equal(panel.popoverOpen, true);
  panel.hidePopover();
  assert.equal(chat.element('contextToggle').attributes.get('aria-expanded'), 'false');
  chat.element('input').value = 'Hello';
  chat.send();
  assert.deepEqual(chat.posts.at(-1).include, ['entity:myth']);
});

test('Preview replaces Context and late replies cannot reopen a dismissed popover', () => {
  const chat = page();
  chat.element('contextToggle').events.get('click')();
  chat.element('preview').onclick();
  const requestId = chat.posts.at(-1).requestId;
  assert.equal(chat.element('context').hidden, true);
  assert.equal(chat.element('previewPanel').popoverOpen, true);
  chat.element('previewPanel').hidePopover();
  chat.message({ type: 'preview', requestId, lines: [], text: 'old preview' });
  assert.equal(chat.element('previewPanel').hidden, true);
  chat.element('preview').onclick();
  chat.message({ type: 'preview', requestId, lines: [], text: 'old preview' });
  assert.equal(chat.element('previewPanel').textContent, 'Loading preview…');
  chat.message({ type: 'preview', requestId: chat.posts.at(-1).requestId, lines: [] });
  assert.equal(chat.element('previewPanel').innerHTML, '<p></p>');
});

test('chat budget controls submit shared settings and show the confirmed allowance', () => {
  const chat = page();
  chat.element('chatContextBudget').value = '20000';
  chat.element('chatContextLength').value = '0';
  chat.element('budgetForm').events.get('submit')({ preventDefault() {} });
  assert.deepEqual(chat.posts.at(-1), { type: 'setBudgets', contextBudget: '20000', contextLength: '0' });
  assert.equal(chat.element('applyBudgets').disabled, true);
  chat.message({ type: 'budgets', budgets: {
    contextBudget: 20000, contextLength: 0, minimum: 500, maximum: 2000000, inputBudget: 14952, outputReserve: 2048,
  } });
  assert.equal(chat.element('applyBudgets').disabled, false);
  assert.equal(chat.element('budgetSummary').textContent, '14,952 input tokens · 2,048 reserved for the answer');
  chat.element('budgetForm').events.get('submit')({ preventDefault() {} });
  chat.message({ type: 'budgetsRejected', error: 'Cannot save settings' });
  assert.equal(chat.element('applyBudgets').disabled, false);
  assert.equal(chat.element('budgetSummary').textContent, 'Cannot save settings');
});
