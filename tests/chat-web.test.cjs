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
      this._text = '';
    }
    set textContent(value) { this._text = value; this.children = []; }
    get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
    get childElementCount() { return this.children.length; }
    appendChild(child) { child.parent = this; this.children.push(child); }
    remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); }
    addEventListener(name, handler) { this.events.set(name, handler); }
    setAttribute() {}
    querySelectorAll() { return []; }
  }
  const elements = new Map();
  const element = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  element('context').hidden = true;
  const posts = [];
  const frames = new Map();
  let frameId = 0;
  let receive;
  const context = vm.createContext({
    document: {
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
  chat.element('context').textContent = 'old context';
  chat.element('input').value = 'old draft';
  chat.message({ type: 'state', isGenerating: true, streamingDelta: 'old stream' });
  chat.message({ type: 'history', reset: true, messages: [] });
  chat.flush();
  assert.equal(chat.element('messages').textContent, '');
  assert.equal(chat.element('previewPanel').textContent, '');
  assert.equal(chat.element('previewPanel').hidden, true);
  assert.equal(chat.element('context').textContent, '');
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
