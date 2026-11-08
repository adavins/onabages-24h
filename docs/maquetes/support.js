// Visor mínim per obrir les maquetes (.dc.html) directament al navegador, sense connexió.
// Implementa només el que fan servir aquestes maquetes: forats {{ camí }}, <sc-for>, <sc-if>,
// <helmet>, esdeveniments onClick i la classe DCLogic amb setState.
// No forma part de l'aplicació: és només per veure les maquetes.
(function () {
  'use strict';

  class DCLogic {
    constructor(props) { this.props = props || {}; this.state = {}; }
    setState(patch) {
      const p = typeof patch === 'function' ? patch(this.state, this.props) : patch;
      this.state = Object.assign({}, this.state, p);
      if (this.__render) this.__render();
    }
    forceUpdate() { if (this.__render) this.__render(); }
    renderVals() { return {}; }
  }
  window.DCLogic = DCLogic;

  const HOLE = /\{\{\s*([^}]+?)\s*\}\}/g;
  const WHOLE = /^\s*\{\{\s*([^}]+?)\s*\}\}\s*$/;

  // Resol un camí amb punts (o un literal) dins de l'àmbit actual.
  function resolve(expr, scope) {
    const e = expr.trim();
    if (e === 'true') return true;
    if (e === 'false') return false;
    if (e === 'null') return null;
    if (/^-?\d+(\.\d+)?$/.test(e)) return Number(e);
    if (/^'.*'$/.test(e) || /^".*"$/.test(e)) return e.slice(1, -1);
    let v = scope;
    for (const part of e.split('.')) {
      if (v == null) return undefined;
      v = v[part];
    }
    return v;
  }

  function interpolate(text, scope) {
    return text.replace(HOLE, (_, expr) => {
      const v = resolve(expr, scope);
      return v == null ? '' : String(v);
    });
  }

  // Processa un node clonat de la plantilla i en retorna els nodes resultants.
  function processNode(node, scope) {
    if (node.nodeType === Node.TEXT_NODE) {
      if (node.nodeValue.indexOf('{{') >= 0) node.nodeValue = interpolate(node.nodeValue, scope);
      return [node];
    }
    if (node.nodeType !== Node.ELEMENT_NODE) return [node];

    const tag = node.tagName.toLowerCase();

    if (tag === 'sc-for') {
      const list = resolve((node.getAttribute('list') || '').replace(/^\s*\{\{|\}\}\s*$/g, ''), scope) || [];
      const as = node.getAttribute('as') || 'item';
      const out = [];
      list.forEach((item, index) => {
        const inner = Object.assign(Object.create(scope), { [as]: item, $index: index });
        for (const child of Array.from(node.childNodes)) {
          out.push(...processNode(child.cloneNode(true), inner));
        }
      });
      return out;
    }

    if (tag === 'sc-if') {
      const v = resolve((node.getAttribute('value') || '').replace(/^\s*\{\{|\}\}\s*$/g, ''), scope);
      if (!v) return [];
      const out = [];
      for (const child of Array.from(node.childNodes)) out.push(...processNode(child, scope));
      return out;
    }

    // Atributs: forats sencers (valors), interpolats (text) i esdeveniments.
    for (const attr of Array.from(node.attributes)) {
      const val = attr.value;
      if (val.indexOf('{{') < 0) continue;
      const name = attr.name;
      if (name.startsWith('on')) {
        const m = val.match(WHOLE);
        const fn = m ? resolve(m[1], scope) : null;
        node.removeAttribute(name);
        if (typeof fn === 'function') node.addEventListener(name.slice(2), fn);
        continue;
      }
      const m = val.match(WHOLE);
      const out = m ? resolve(m[1], scope) : interpolate(val, scope);
      if (out === false || out == null) node.removeAttribute(name);
      else node.setAttribute(name, out === true ? '' : String(out));
    }

    const children = Array.from(node.childNodes);
    for (const child of children) {
      const replaced = processNode(child, scope);
      if (replaced.length === 1 && replaced[0] === child) continue;
      for (const r of replaced) node.insertBefore(r, child);
      if (replaced.indexOf(child) < 0) child.remove();
    }
    return [node];
  }

  function mount() {
    const host = document.querySelector('x-dc');
    if (!host) return;

    // <helmet>: el seu contingut va al <head> una sola vegada.
    const helmet = host.querySelector('helmet');
    if (helmet) {
      for (const child of Array.from(helmet.childNodes)) document.head.appendChild(child);
      helmet.remove();
    }

    const template = document.createElement('template');
    template.innerHTML = host.innerHTML;
    host.innerHTML = '';

    const script = document.querySelector('script[data-dc-script]');
    let props = {};
    try { props = JSON.parse(script.getAttribute('data-props') || '{}'); } catch (e) { props = {}; }
    const defaults = {};
    for (const k of Object.keys(props)) if (props[k] && 'default' in props[k]) defaults[k] = props[k].default;

    const Component = new Function('DCLogic', script.textContent + '\nreturn Component;')(DCLogic);
    const instance = new Component(defaults);
    instance.props = instance.props || defaults;

    instance.__render = function () {
      const vals = instance.renderVals() || {};
      const frag = template.content.cloneNode(true);
      const out = [];
      for (const child of Array.from(frag.childNodes)) out.push(...processNode(child, vals));
      host.replaceChildren(...out);
    };
    instance.__render();
    if (typeof instance.componentDidMount === 'function') instance.componentDidMount();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount);
  else mount();
})();
