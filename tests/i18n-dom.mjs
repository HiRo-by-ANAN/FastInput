import vm from 'node:vm';

class Events {
  listeners = new Map();
  addEventListener(type, callback) {
    const callbacks = this.listeners.get(type) || [];
    callbacks.push(callback);
    this.listeners.set(type, callbacks);
  }
  removeEventListener(type, callback) {
    this.listeners.set(type, (this.listeners.get(type) || []).filter(item => item !== callback));
  }
  dispatchEvent(event) {
    event.target ||= this;
    for (const callback of this.listeners.get(event.type) || []) callback(event);
    return true;
  }
}
class Element extends Events {
  constructor(tagName) {
    super();
    this.tagName = tagName.toUpperCase();
    this.attributes = new Map();
    this.children = [];
    this.dataset = {};
    this.style = {};
    this.value = '';
    this.hidden = false;
    this.selectionStart = this.selectionEnd = 0;
    this._text = '';
    this.className = '';
    this.classList = {
      add: (...names) => { this.className = [...new Set([...this.className.split(' '), ...names])].join(' '); },
      remove: (...names) => { this.className = this.className.split(' ').filter(name => !names.includes(name)).join(' '); },
      contains: name => this.className.split(' ').includes(name),
      toggle: (name, force) => {
        const enabled = force ?? !this.classList.contains(name);
        this.classList[enabled ? 'add' : 'remove'](name);
        return enabled;
      },
    };
  }
  get id() { return this.getAttribute('id') || ''; }
  set id(value) { this.setAttribute('id', value); }
  get textContent() { return this._text + this.children.map(child => child.textContent).join(''); }
  set textContent(value) { this._text = String(value ?? ''); this.children = []; }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name) ?? null; }
  removeAttribute(name) { this.attributes.delete(name); }
  append(...children) {
    for (let child of children) {
      if (typeof child === 'string') { const text = new Element('span'); text.textContent = child; child = text; }
      child.parentNode = this;
      this.children.push(child);
    }
  }
  after(child) {
    child.parentNode = this.parentNode;
    this.parentNode.children.splice(this.parentNode.children.indexOf(this) + 1, 0, child);
  }
  appendChild(child) { this.append(child); return child; }
  replaceChildren(...children) { this.children = []; this._text = ''; this.append(...children); }
  remove() { if (this.parentNode) this.parentNode.children = this.parentNode.children.filter(child => child !== this); }
  focus() {}
  select() {}
  setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
  setPointerCapture() {}
  releasePointerCapture() {}
  querySelectorAll(selector) {
    const result = [];
    const visit = element => {
      for (const child of element.children) {
        if (selector.startsWith('[') && child.attributes.has(selector.slice(1, -1))) result.push(child);
        visit(child);
      }
    };
    visit(this);
    return result;
  }
}
function parseDocument(html) {
  const document = new Element('document');
  const stack = [document];
  const source = html.replace(/<style\b[^>]*>[\s\S]*?<\/style>/gi, '').replace(/<script\b[^>]*>[\s\S]*?<\/script>/gi, '');
  for (const token of source.match(/<[^>]*>|[^<]+/g) || []) {
    if (token.startsWith('</')) { if (stack.length > 1) stack.pop(); continue; }
    if (token.startsWith('<!')) continue;
    if (token.startsWith('<')) {
      const tag = token.match(/^<(\w+)/)?.[1];
      if (!tag) continue;
      const element = new Element(tag);
      for (const match of token.matchAll(/([\w-]+)="([^"]*)"/g)) element.setAttribute(match[1], match[2]);
      element.hidden = /\shidden(?:\s|>)/.test(token);
      stack.at(-1).append(element);
      if (!['meta', 'link', 'input', 'img', 'br', 'hr'].includes(tag) && !token.endsWith('/>')) stack.push(element);
    } else stack.at(-1)._text += token;
  }
  document.documentElement = document.children.find(child => child.tagName === 'HTML');
  document.body = document.documentElement.children.find(child => child.tagName === 'BODY');
  document.getElementById = id => {
    const visit = element => element.id === id ? element : element.children.map(visit).find(Boolean);
    return visit(document);
  };
  document.createElement = tag => new Element(tag);
  document.createElementNS = (_, tag) => new Element(tag);
  document.execCommand = () => true;
  document.readyState = 'loading';
  document.hidden = false;
  return document;
}
export function createHarness(html, options = {}) {
  const document = parseDocument(html);
  const storage = new Map(options.stored ? [['fastinput.language', options.stored]] : []);
  const native = new Events();
  const sent = [];
  native.postMessage = message => sent.push(message);
  const window = new Events();
  window.chrome = { webview: native };
  const requests = [];
  const responses = new Map();
  const timers = new Map();
  let timerId = 0;
  const context = vm.createContext({
    window, document, navigator: { language: options.language, userAgent: 'test', platform: 'test' },
    location: { href: 'http://127.0.0.1:8765/', search: '', pathname: '/' },
    localStorage: {
      getItem: key => { if (options.storageThrows) throw Error('storage blocked'); return storage.get(key) ?? null; },
      setItem: (key, value) => { if (options.storageThrows) throw Error('storage blocked'); storage.set(key, value); },
    },
    CustomEvent: class { constructor(type, properties = {}) { this.type = type; Object.assign(this, properties); } },
    URL, URLSearchParams, Intl, TextEncoder, AbortController, console, performance,
    setTimeout: callback => { timers.set(++timerId, callback); return timerId; },
    clearTimeout: id => timers.delete(id),
    setInterval: () => ++timerId, clearInterval: () => {},
    requestAnimationFrame: () => ++timerId, cancelAnimationFrame: () => {},
    fetch: async (url, init) => {
      requests.push([url, init]);
      const response = responses.get(url) || { status: 200, data: {} };
      return { ok: response.status >= 200 && response.status < 300, status: response.status,
        json: async () => structuredClone(response.data) };
    },
  });
  window.location = context.location;
  window.navigator = context.navigator;
  return {
    context, document, window, native, sent, storage, responses, requests, timers,
    run: (source, filename) => vm.runInContext(source, context, { filename }),
    ready() { document.readyState = 'complete'; document.dispatchEvent({ type: 'DOMContentLoaded' }); },
    async settle() { for (let i = 0; i < 12; i++) await Promise.resolve(); },
  };
}
