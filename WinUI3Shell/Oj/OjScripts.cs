namespace WinUI3Shell.Oj;

/// <summary>注入到后台浏览器每个页面的公共工具脚本。</summary>
internal static class OjScripts
{
    /// <summary>
    /// 提供 __mny.htmlToMd(element)：把题面 DOM 转成 Markdown（保留段落、列表、代码块、表格、行内样式）。
    /// OJ 的抓取脚本只需要挑出题面容器，转换交给这里，避免每个 OJ 写一份。
    /// </summary>
    public const string Prelude = """
(function () {
  function inlineText(el) {
    let out = '';
    for (let i = 0; i < el.childNodes.length; i++) {
      const n = el.childNodes[i];
      if (n.nodeType === 3) { out += n.nodeValue.replace(/\s+/g, ' '); continue; }
      if (n.nodeType !== 1) continue;
      const tag = n.tagName.toLowerCase();
      if (tag === 'br') { out += '\n'; continue; }
      if (tag === 'strong' || tag === 'b') { out += '**' + inlineText(n).trim() + '**'; continue; }
      if (tag === 'em' || tag === 'i') { out += '*' + inlineText(n).trim() + '*'; continue; }
      if (tag === 'code') { out += '`' + n.textContent + '`'; continue; }
      if (tag === 'img') {
        const src = n.getAttribute('src') || '';
        if (src) out += '![](' + src + ')';
        continue;
      }
      if (tag === 'a') {
        const text = inlineText(n).trim();
        const href = n.getAttribute('href') || '';
        out += text ? (href ? '[' + text + '](' + href + ')' : text) : '';
        continue;
      }
      if (tag === 'script' || tag === 'style') continue;
      out += inlineText(n);
    }
    return out;
  }

  function hasBlock(el) {
    return !!el.querySelector('p,div,section,pre,ul,ol,table,h1,h2,h3,h4,h5,h6,hr');
  }

  function toMarkdown(root) {
    const lines = [];
    function push(text) { lines.push(text); }

    function walk(node) {
      for (let i = 0; i < node.childNodes.length; i++) {
        const n = node.childNodes[i];
        // 混合内容容器里直接挂在块级子元素旁边的文字（如 CF 的 time limit 值），
        // 只遍历元素节点会把它们丢掉，这里当成独立段落补回来
        if (n.nodeType === 3) {
          const text = n.nodeValue.replace(/\s+/g, ' ').trim();
          if (text) { push(''); push(text); push(''); }
          continue;
        }
        if (n.nodeType !== 1) continue;
        const tag = n.tagName.toLowerCase();
        if (tag === 'script' || tag === 'style' || tag === 'noscript' || tag === 'svg') continue;

        if (/^h[1-6]$/.test(tag)) {
          const level = Number(tag.charAt(1));
          const text = inlineText(n).trim();
          if (text) { push(''); push('#'.repeat(level) + ' ' + text); push(''); }
          continue;
        }
        if (tag === 'pre') {
          push('');
          push('```');
          push(n.textContent.replace(/\s+$/, ''));
          push('```');
          push('');
          continue;
        }
        if (tag === 'ul' || tag === 'ol') {
          const items = n.children;
          for (let k = 0; k < items.length; k++) {
            if (items[k].tagName.toLowerCase() !== 'li') continue;
            push((tag === 'ol' ? (k + 1) + '. ' : '- ') + inlineText(items[k]).trim());
          }
          push('');
          continue;
        }
        if (tag === 'table') {
          const rows = n.querySelectorAll('tr');
          for (let r = 0; r < rows.length; r++) {
            const cells = rows[r].querySelectorAll('th,td');
            if (!cells.length) continue;
            const values = [];
            for (let c = 0; c < cells.length; c++) {
              values.push(inlineText(cells[c]).trim().replace(/\|/g, '\\|'));
            }
            push('| ' + values.join(' | ') + ' |');
            if (r === 0 && rows[r].querySelector('th')) {
              push('|' + values.map(function () { return ' --- |'; }).join(''));
            }
          }
          push('');
          continue;
        }
        if (tag === 'hr') { push(''); push('---'); push(''); continue; }
        if (tag === 'p') {
          const text = inlineText(n).trim();
          if (text) { push(''); push(text); push(''); }
          continue;
        }
        if (tag === 'section' || tag === 'div' || tag === 'article' || tag === 'main' || tag === 'blockquote') {
          if (hasBlock(n)) {
            walk(n);
          } else {
            const text = inlineText(n).trim();
            if (text) { push(''); push(text); push(''); }
          }
          continue;
        }
        if (tag === 'li') {
          const text = inlineText(n).trim();
          if (text) push('- ' + text);
          continue;
        }
        walk(n);
      }
    }

    walk(root);
    return lines.join('\n')
      .replace(/[ \t]+\n/g, '\n')
      .replace(/\n{3,}/g, '\n\n')
      .trim();
  }

  window.__mny = {
    htmlToMd: toMarkdown,
    inlineText: inlineText
  };

  // 有些站点（如牛客）把首屏数据放在 window.__INITIAL_STATE__，页面框架水合后会删掉这个变量。
  // 公共脚本在文档创建时就注入，趁 DOMContentLoaded（早于页面自己的水合逻辑）先存一份快照。
  function snapshotState() {
    try {
      if (!window.__mny.state && window.__INITIAL_STATE__) {
        window.__mny.state = window.__INITIAL_STATE__;
      }
    } catch (e) { /* 快照失败不影响主流程 */ }
  }
  document.addEventListener('DOMContentLoaded', snapshotState);
  window.addEventListener('load', snapshotState);
})();
""";
}
