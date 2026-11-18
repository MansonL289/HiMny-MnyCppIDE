require.config({ paths: { vs: './vs' } });

require(['vs/editor/editor.main'], function () {
  'use strict';

  const host = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;
  const tabsEl = document.getElementById('tabs');
  const largeFileNoticeEl = document.getElementById('largeFileNotice');
  const settingsKey = 'mny-editor-settings';

  function readSettings() {
    try { return JSON.parse(localStorage.getItem(settingsKey) || '{}'); } catch (e) { return {}; }
  }
  function writeSettings() {
    // 只缓存外观相关的项，模板代码等大文本不写进 localStorage
    try {
      localStorage.setItem(settingsKey, JSON.stringify({
        theme: settings.theme,
        uiZoom: settings.uiZoom,
        fontFamily: settings.fontFamily,
        fontSize: settings.fontSize,
        fontLigatures: settings.fontLigatures,
        wordWrap: settings.wordWrap,
        minimap: settings.minimap,
        whitespace: settings.whitespace,
        cursorStyle: settings.cursorStyle,
        cursorBlinking: settings.cursorBlinking,
        tabSize: settings.tabSize,
        insertSpaces: settings.insertSpaces
      }));
    } catch (e) { }
  }

  const settings = Object.assign(
    {
      theme: 'system',
      uiZoom: 1,
      fontFamily: 'Cascadia Mono, Consolas, "Microsoft YaHei UI", monospace',
      fontSize: 13,
      fontLigatures: true,
      wordWrap: 'off',
      minimap: true,
      whitespace: 'selection',
      cursorStyle: 'line',
      cursorBlinking: 'blink',
      tabSize: 4,
      insertSpaces: true,
      suggest: true,
      parameterHints: true,
      hover: true,
      bracketColorization: true,
      stickyScroll: true,
      occurrencesHighlight: true,
      autoSave: false,
      autoSaveSeconds: 60
    },
    readSettings()
  );

  const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');

  function isDarkTheme() {
    if (settings.theme === 'dark') { return true; }
    if (settings.theme === 'light') { return false; }
    return darkQuery.matches;
  }

  function applyTheme() {
    const dark = isDarkTheme();
    monaco.editor.setTheme(dark ? 'mny-dark' : 'vs');
    // 页面外壳（标签栏、终端）跟随同一个主题
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  }

  /* 自定义深色主题：One Dark 风格的 C++ 语法配色 */
  monaco.editor.defineTheme('mny-dark', {
    base: 'vs-dark',
    inherit: true,
    rules: [
      { token: 'comment', foreground: '5c6370', fontStyle: 'italic' },
      { token: 'keyword', foreground: 'c678dd' },
      { token: 'keyword.directive', foreground: 'c678dd' },
      { token: 'string', foreground: '98c379' },
      { token: 'string.escape', foreground: '56b6c2' },
      // #include <...> 里的头文件名用蓝色
      { token: 'string.include', foreground: '61afef' },
      { token: 'string.include.identifier', foreground: '61afef' },
      { token: 'number', foreground: 'd19a66' },
      { token: 'typeid', foreground: 'e5c07b' },
      { token: 'typename', foreground: 'e5c07b' },
      { token: 'typedef', foreground: 'e5c07b' },
      { token: 'operator', foreground: '56b6c2' },
      { token: 'delimiter', foreground: 'a6adba' },
      { token: 'identifier', foreground: 'e4e7ee' }
    ],
    colors: {
      'editor.background': '#1e2027',
      'editor.foreground': '#d7dae0',
      'editorLineNumber.foreground': '#4b5263',
      'editorLineNumber.activeForeground': '#9aa0aa',
      'editorCursor.foreground': '#61afef',
      'editor.selectionBackground': '#3e4451',
      'editor.lineHighlightBackground': '#26282f',
      'editorIndentGuide.background1': '#2c2f3a',
      'editorGutter.background': '#1e2027'
    }
  });

  const editor = monaco.editor.create(document.getElementById('editor'), {
    theme: isDarkTheme() ? 'mny-dark' : 'vs',
    value: '',
    language: 'plaintext',
    automaticLayout: true,
    fontSize: settings.fontSize,
    fontFamily: settings.fontFamily,
    fontLigatures: settings.fontLigatures,
    wordWrap: settings.wordWrap,
    minimap: { enabled: settings.minimap },
    // 行号左侧留出装订区，用来显示断点红点
    glyphMargin: true,
    renderWhitespace: settings.whitespace,
    cursorStyle: settings.cursorStyle,
    cursorBlinking: settings.cursorBlinking,
    tabSize: settings.tabSize,
    insertSpaces: settings.insertSpaces,
    scrollBeyondLastLine: false,
    // 补全/悬浮等弹层用 fixed 定位，避免被编辑器容器的圆角裁剪裁掉
    fixedOverflowWidgets: true,
    // 补全：输入时自动弹出，并开启当前文档内的单词补全
    quickSuggestions: { other: true, comments: false, strings: false },
    suggestOnTriggerCharacters: true,
    wordBasedSuggestions: true,
    acceptSuggestionOnEnter: 'on',
    tabCompletion: 'on',
    snippetSuggestions: 'top',
    parameterHints: { enabled: settings.parameterHints },
    suggest: { showKeywords: true, showSnippets: true }
  });

  darkQuery.addEventListener('change', function () {
    if (settings.theme === 'system') { applyTheme(); }
  });

  const LANGUAGE_BY_EXT = {
    cpp: 'cpp', cc: 'cpp', cxx: 'cpp', c: 'cpp', h: 'cpp', hpp: 'cpp', hxx: 'cpp', inl: 'cpp',
    cs: 'csharp', py: 'python', js: 'javascript', mjs: 'javascript', cjs: 'javascript',
    ts: 'typescript', json: 'json', md: 'markdown', html: 'html', htm: 'html', css: 'css',
    scss: 'scss', less: 'less', xml: 'xml', java: 'java', rs: 'rust', go: 'go',
    sh: 'shell', bat: 'bat', cmd: 'bat', ps1: 'powershell', yml: 'yaml', yaml: 'yaml',
    ini: 'ini', toml: 'ini', sql: 'sql', txt: 'plaintext'
  };

  function languageFor(name) {
    const ext = String(name || '').split('.').pop().toLowerCase();
    return LANGUAGE_BY_EXT[ext] || 'plaintext';
  }

  /* ---------- C++ 代码补全 ---------- */

  const CPP_KEYWORDS = [
    'alignas', 'alignof', 'auto', 'bool', 'break', 'case', 'catch', 'char', 'class',
    'const', 'const_cast', 'constexpr', 'continue', 'decltype', 'default', 'delete',
    'do', 'double', 'dynamic_cast', 'else', 'enum', 'explicit', 'extern', 'false',
    'float', 'for', 'friend', 'goto', 'if', 'inline', 'int', 'long', 'mutable',
    'namespace', 'new', 'noexcept', 'nullptr', 'operator', 'private', 'protected',
    'public', 'reinterpret_cast', 'return', 'short', 'sizeof', 'static', 'static_cast',
    'struct', 'switch', 'template', 'throw', 'true', 'try', 'typedef', 'typeid',
    'typename', 'union', 'unsigned', 'using', 'virtual', 'void', 'volatile', 'while',
    'std', 'cout', 'cin', 'cerr', 'endl', 'string', 'vector', 'map', 'set', 'pair',
    'size_t', 'uint8_t', 'uint16_t', 'uint32_t', 'uint64_t', 'int32_t', 'int64_t',
    'printf', 'scanf', 'puts', 'getchar', 'malloc', 'calloc', 'realloc', 'free',
    'assert', 'exit', 'main'
  ];

  const CPP_HEADERS = [
    'iostream', 'iomanip', 'ios', 'fstream', 'sstream', 'string', 'string_view',
    'cstring', 'cstdio', 'cstdlib', 'cmath', 'ctime', 'algorithm', 'array', 'bitset',
    'chrono', 'deque', 'functional', 'iterator', 'limits', 'list', 'map', 'memory',
    'numeric', 'optional', 'queue', 'random', 'set', 'stack', 'stdexcept', 'thread',
    'tuple', 'type_traits', 'unordered_map', 'unordered_set', 'utility', 'variant',
    'vector', 'cstdint', 'stdint.h', 'stdio.h', 'stdlib.h', 'string.h', 'math.h',
    'time.h', 'windows.h'
  ];

  const CPP_SNIPPETS = [
    {
      label: 'main',
      detail: 'int main()',
      insertText: 'int main() {\n\t$0\n\treturn 0;\n}'
    },
    {
      label: 'for',
      detail: 'for (...)',
      insertText: 'for (int ${1:i} = 0; ${1:i} < ${2:count}; ++${1:i}) {\n\t$0\n}'
    },
    {
      label: 'forr',
      detail: 'for (auto& x : ...)',
      insertText: 'for (auto& ${1:item} : ${2:container}) {\n\t$0\n}'
    },
    {
      label: 'while',
      detail: 'while (...)',
      insertText: 'while (${1:condition}) {\n\t$0\n}'
    },
    {
      label: 'if',
      detail: 'if (...)',
      insertText: 'if (${1:condition}) {\n\t$0\n}'
    },
    {
      label: 'ifelse',
      detail: 'if / else',
      insertText: 'if (${1:condition}) {\n\t${2}\n} else {\n\t$0\n}'
    },
    {
      label: 'switch',
      detail: 'switch (...)',
      insertText: 'switch (${1:value}) {\n\tcase ${2:0}:\n\t\t$0\n\t\tbreak;\n\tdefault:\n\t\tbreak;\n}'
    },
    {
      label: 'class',
      detail: 'class ... {}',
      insertText: 'class ${1:Name} {\npublic:\n\t${1:Name}() = default;\n\t~${1:Name}() = default;\n\nprivate:\n\t$0\n};'
    },
    {
      label: 'struct',
      detail: 'struct ... {}',
      insertText: 'struct ${1:Name} {\n\t$0\n};'
    },
    {
      label: 'func',
      detail: '返回类型 函数名(...) {}',
      insertText: '${1:void} ${2:functionName}(${3}) {\n\t$0\n}'
    },
    {
      label: 'cout',
      detail: 'std::cout <<',
      insertText: 'std::cout << ${1} << std::endl;'
    },
    {
      label: 'cin',
      detail: 'std::cin >>',
      insertText: 'std::cin >> ${1};'
    },
    {
      label: 'include',
      detail: '#include <...>',
      insertText: '#include <${1:iostream}>'
    }
  ];

  monaco.languages.registerCompletionItemProvider('cpp', {
    triggerCharacters: ['#', '<', '"', '.', ':'],
    provideCompletionItems: function (model, position) {
      const linePrefix = model.getLineContent(position.lineNumber).slice(0, position.column - 1);

      // 正在写 #include <...> / #include "..." 时只补头文件名
      const includeMatch = /^[ \t]*#[ \t]*include[ \t]*([<"])([\w./+-]*)$/.exec(linePrefix);
      if (includeMatch) {
        const opener = includeMatch[1];
        const partial = includeMatch[2];
        const startColumn = position.column - partial.length;
        const range = {
          startLineNumber: position.lineNumber,
          endLineNumber: position.lineNumber,
          startColumn: startColumn,
          endColumn: position.column
        };
        const closer = opener === '<' ? '>' : '"';
        return {
          suggestions: CPP_HEADERS
            .filter(function (name) { return partial === '' || name.indexOf(partial) !== -1; })
            .map(function (name) {
              return {
                label: name,
                kind: monaco.languages.CompletionItemKind.File,
                detail: opener + name + closer,
                insertText: name + closer,
                range: range
              };
            })
        };
      }

      const word = model.getWordUntilPosition(position);
      const range = {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endColumn: word.endColumn
      };

      const suggestions = [];

      CPP_SNIPPETS.forEach(function (snippet) {
        suggestions.push({
          label: snippet.label,
          kind: monaco.languages.CompletionItemKind.Snippet,
          detail: snippet.detail,
          documentation: snippet.detail,
          insertText: snippet.insertText,
          insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
          sortText: '0_' + snippet.label,
          range: range
        });
      });

      CPP_KEYWORDS.forEach(function (keyword) {
        suggestions.push({
          label: keyword,
          kind: monaco.languages.CompletionItemKind.Keyword,
          insertText: keyword,
          sortText: '1_' + keyword,
          range: range
        });
      });

      return { suggestions: suggestions };
    }
  });

  /* ---------- C++ 语义着色与检查 ----------
     语法包只给关键字、字符串、数字上色，函数名、std::、cout 这些都只是普通标识符。
     这里在 tokenize 结果之上再补一层：函数调用着色、std 与类型着色、
     库符号按是否 #include 了对应头文件决定着色，缺头文件时给一条警告。 */

  // 语法包里没收录的较新关键字
  const CPP_EXTRA_KEYWORDS = new Set([
    'alignas', 'asm', 'char8_t', 'char16_t', 'char32_t', 'concept', 'consteval',
    'constinit', 'co_await', 'co_return', 'co_yield', 'requires', 'static_assert',
    'noexcept'
  ]);

  // 头文件 -> 该头文件提供的符号；没 include 就不着色
  const CPP_LIB_HEADERS = {
    iostream: ['cout', 'cin', 'cerr', 'clog', 'endl', 'ends', 'flush'],
    iomanip: ['setw', 'setprecision', 'setfill', 'fixed', 'scientific'],
    cstdio: ['printf', 'scanf', 'sprintf', 'snprintf', 'sscanf', 'puts', 'getchar',
      'putchar', 'fopen', 'fclose', 'fprintf', 'fgets', 'fputs', 'fgetc', 'fputc',
      'fflush', 'perror', 'EOF', 'FILE', 'stdin', 'stdout', 'stderr'],
    cstdlib: ['malloc', 'calloc', 'realloc', 'free', 'atoi', 'atof', 'atol', 'rand',
      'srand', 'qsort', 'exit', 'abort', 'system', 'getenv', 'EXIT_SUCCESS',
      'EXIT_FAILURE', 'RAND_MAX'],
    cstring: ['strlen', 'strcpy', 'strncpy', 'strcmp', 'strncmp', 'strcat', 'strstr',
      'strchr', 'memset', 'memcpy', 'memmove', 'memcmp'],
    cmath: ['sqrt', 'pow', 'exp', 'log', 'log10', 'sin', 'cos', 'tan', 'asin', 'acos',
      'atan', 'atan2', 'fabs', 'floor', 'ceil', 'round', 'fmod', 'M_PI', 'INFINITY'],
    ctime: ['time', 'clock', 'difftime', 'localtime', 'gmtime', 'strftime', 'mktime',
      'time_t', 'CLOCKS_PER_SEC'],
    cassert: ['assert'],
    cstdint: ['int8_t', 'int16_t', 'int32_t', 'int64_t', 'uint8_t', 'uint16_t',
      'uint32_t', 'uint64_t', 'intptr_t', 'uintptr_t'],
    cstddef: ['size_t', 'ptrdiff_t'],
    string: ['string', 'string_view', 'to_string', 'stoi', 'stol', 'stod', 'stof', 'getline'],
    vector: ['vector'], array: ['array'], list: ['list'], deque: ['deque'],
    stack: ['stack'], queue: ['queue', 'priority_queue'],
    map: ['map', 'multimap', 'unordered_map'], set: ['set', 'multiset', 'unordered_set'],
    utility: ['pair', 'make_pair', 'move', 'forward'],
    memory: ['unique_ptr', 'shared_ptr', 'weak_ptr', 'make_unique', 'make_shared'],
    fstream: ['ifstream', 'ofstream', 'fstream'],
    sstream: ['stringstream', 'istringstream', 'ostringstream'],
    thread: ['thread', 'mutex', 'lock_guard'],
    bitset: ['bitset'], optional: ['optional'], variant: ['variant'], tuple: ['tuple'],
    stdexcept: ['runtime_error', 'logic_error', 'out_of_range', 'invalid_argument']
  };

  // 等价头文件：<stdio.h> 与 <cstdio> 视为同一个
  const CPP_HEADER_ALIASES = {
    cstdio: ['stdio.h'], cstdlib: ['stdlib.h'], cstring: ['string.h'], cmath: ['math.h'],
    ctime: ['time.h'], cstdint: ['stdint.h'], cstddef: ['stddef.h'], cassert: ['assert.h']
  };

  const CPP_TYPE_SYMBOLS = new Set([
    'FILE', 'size_t', 'ptrdiff_t', 'time_t',
    'int8_t', 'int16_t', 'int32_t', 'int64_t', 'uint8_t', 'uint16_t', 'uint32_t',
    'uint64_t', 'intptr_t', 'uintptr_t',
    'string', 'string_view', 'vector', 'array', 'list', 'deque', 'stack', 'queue',
    'priority_queue', 'map', 'multimap', 'unordered_map', 'set', 'multiset',
    'unordered_set', 'pair', 'unique_ptr', 'shared_ptr', 'weak_ptr', 'ifstream',
    'ofstream', 'fstream', 'stringstream', 'istringstream', 'ostringstream', 'thread',
    'mutex', 'lock_guard', 'bitset', 'optional', 'variant', 'tuple', 'runtime_error',
    'logic_error', 'out_of_range', 'invalid_argument'
  ]);

  // 缺头文件时值得提醒的符号（用户几乎不会自己定义同名函数）
  const CPP_CHECK_SYMBOLS = new Set([
    'printf', 'scanf', 'sprintf', 'snprintf', 'puts', 'fopen', 'fclose', 'fputs',
    'fgets', 'fprintf', 'perror', 'malloc', 'calloc', 'realloc', 'free', 'atoi',
    'atof', 'qsort', 'strlen', 'strcpy', 'strncpy', 'strcmp', 'strcat', 'memset',
    'memcpy', 'sqrt', 'pow', 'fabs', 'cout', 'cin', 'cerr', 'clog', 'endl'
  ]);

  // 基础类型关键字：用类型色，避免所有关键字一个颜色
  const CPP_TYPE_WORDS = new Set([
    'void', 'bool', 'char', 'short', 'int', 'long', 'float', 'double', 'signed',
    'unsigned', 'wchar_t', 'char8_t', 'char16_t', 'char32_t', 'auto', 'size_t'
  ]);

  const CPP_SYMBOL_INFO = new Map();
  Object.keys(CPP_LIB_HEADERS).forEach(function (header) {
    CPP_LIB_HEADERS[header].forEach(function (symbol) {
      let info = CPP_SYMBOL_INFO.get(symbol);
      if (!info) {
        info = { headers: [], type: CPP_TYPE_SYMBOLS.has(symbol) };
        CPP_SYMBOL_INFO.set(symbol, info);
      }
      // 同一个符号可能由多个头文件提供（比如 getline），任意一个 included 即可
      if (info.headers.indexOf(header) === -1) { info.headers.push(header); }
    });
  });

  function libHeaderIncluded(headers, info) {
    for (let i = 0; i < info.headers.length; i += 1) {
      if (headerIncluded(headers, info.headers[i])) { return true; }
    }
    return false;
  }

  function headerIncluded(headers, header) {
    if (headers.has('*') || headers.has(header)) { return true; }
    const aliases = CPP_HEADER_ALIASES[header];
    if (!aliases) { return false; }
    for (let i = 0; i < aliases.length; i += 1) {
      if (headers.has(aliases[i])) { return true; }
    }
    return false;
  }

  function tokenOffset(token) {
    // Monaco 0.52 用 offset，旧版本用 startIndex
    return typeof token.offset === 'number' ? token.offset : token.startIndex;
  }

  function previousMeaningfulToken(flat, index) {
    for (let i = index - 1; i >= 0; i -= 1) {
      if (flat[i].word.trim()) { return flat[i]; }
    }
    return null;
  }

  // ---- 函数名识别（纯文本）开始 ----
  // 把注释和字符串用空格覆盖掉，长度和换行保持不变，避免把里面的内容当代码
  function maskCppNonCode(text) {
    const out = text.split('');
    const n = text.length;
    let i = 0;
    while (i < n) {
      const ch = text[i];
      const nxt = text[i + 1];
      if (ch === '/' && nxt === '/') {
        while (i < n && text[i] !== '\n') { out[i] = ' '; i += 1; }
      } else if (ch === '/' && nxt === '*') {
        out[i] = ' '; out[i + 1] = ' '; i += 2;
        while (i < n && !(text[i] === '*' && text[i + 1] === '/')) {
          if (text[i] !== '\n') { out[i] = ' '; }
          i += 1;
        }
        if (i < n) { out[i] = ' '; out[i + 1] = ' '; i += 2; }
      } else if (ch === '"' || ch === "'") {
        const quote = ch;
        out[i] = ' '; i += 1;
        while (i < n) {
          if (text[i] === '\\') {
            out[i] = ' ';
            if (i + 1 < n && text[i + 1] !== '\n') { out[i + 1] = ' '; }
            i += 2;
            continue;
          }
          if (text[i] === quote) { out[i] = ' '; i += 1; break; }
          if (text[i] !== '\n') { out[i] = ' '; }
          i += 1;
        }
      } else {
        i += 1;
      }
    }
    return out.join('');
  }

  // 这些词出现在名字前面时只能说明是语句，不能当成「类型 名字(」
  const CPP_NOT_TYPE_WORDS = new Set([
    'return', 'if', 'else', 'while', 'for', 'switch', 'case', 'default', 'do',
    'break', 'continue', 'goto', 'throw', 'new', 'delete', 'sizeof', 'catch',
    'try', 'using', 'typedef', 'namespace', 'co_return', 'co_yield', 'co_await',
    'static_assert', 'operator', 'and', 'or', 'not'
  ]);

  function offsetToPosition(lineStarts, offset) {
    let low = 0;
    let high = lineStarts.length - 1;
    while (low < high) {
      const mid = Math.floor((low + high + 1) / 2);
      if (lineStarts[mid] <= offset) { low = mid; } else { high = mid - 1; }
    }
    return { line: low + 1, column: offset - lineStarts[low] + 1 };
  }

  // 返回所有需要着色的文本区间：函数名 / 变量名
  function collectTextSymbols(text) {
    const masked = maskCppNonCode(text);
    const ranges = [];
    const seen = new Set();
    function push(start, end, kind) {
      if (seen.has(start)) { return; }
      seen.add(start);
      ranges.push({ start: start, end: end, kind: kind });
    }

    // 函数：类型 名字(
    const funcNames = new Set();
    const decl = /([A-Za-z_]\w*)[ \t]+([A-Za-z_]\w*)[ \t]*\(/g;
    let m = decl.exec(masked);
    while (m !== null) {
      const typeWord = m[1];
      const name = m[2];
      if (!CPP_NOT_TYPE_WORDS.has(typeWord) && !CPP_NOT_TYPE_WORDS.has(name)) {
        funcNames.add(name);
        const start = m.index + m[0].lastIndexOf(name);
        push(start, start + name.length, 'function');
      }
      m = decl.exec(masked);
    }

    // 调用：已声明过的函数，或者 obj.foo( / obj->foo( / Foo::bar(
    const call = /([A-Za-z_]\w*)[ \t]*\(/g;
    m = call.exec(masked);
    while (m !== null) {
      const name = m[1];
      let hit = funcNames.has(name);
      if (!hit) {
        let k = m.index - 1;
        while (k >= 0 && (masked[k] === ' ' || masked[k] === '\t')) { k -= 1; }
        hit = k >= 0 && (masked[k] === '.' || masked[k] === ':');
      }
      if (hit) {
        push(m.index, m.index + name.length, 'function');
      }
      m = call.exec(masked);
    }

    // 变量：类型 名字 后面紧跟 = ; , ) [ : {
    const varNames = new Set();
    // 同一句声明里逗号后面的变量（int a, b, c;）不会单独匹配到，
    // 需要从第一个变量往后扫，否则只有 a 被当成变量着色，b 一直是普通标识符
    function collectCommaDeclarators(from) {
      let i = from;
      let depth = 0;
      while (i < masked.length) {
        const ch = masked[i];
        if (ch === ';') { break; }
        if (ch === '(' || ch === '[' || ch === '{') { depth += 1; i += 1; continue; }
        if (ch === ')' || ch === ']' || ch === '}') {
          if (depth === 0) { break; }
          depth -= 1; i += 1; continue;
        }
        if (ch === ',' && depth === 0) {
          i += 1;
          while (i < masked.length && /[ \t\r\n*&]/.test(masked[i])) { i += 1; }
          const more = /^([A-Za-z_]\w*)/.exec(masked.slice(i));
          if (!more) { continue; }
          const moreName = more[1];
          if (!CPP_NOT_TYPE_WORDS.has(moreName) && !funcNames.has(moreName)) {
            varNames.add(moreName);
            push(i, i + moreName.length, 'variable');
          }
          i += moreName.length;
          continue;
        }
        i += 1;
      }
    }

    const vdecl = /([A-Za-z_]\w*)[ \t]+([A-Za-z_]\w*)[ \t]*([=;,)\[:{])/g;
    m = vdecl.exec(masked);
    while (m !== null) {
      const typeWord = m[1];
      const name = m[2];
      if (!CPP_NOT_TYPE_WORDS.has(typeWord) && !CPP_NOT_TYPE_WORDS.has(name) && !funcNames.has(name)) {
        varNames.add(name);
        const start = m.index + m[0].lastIndexOf(name);
        push(start, start + name.length, 'variable');
        collectCommaDeclarators(start + name.length);
      }
      m = vdecl.exec(masked);
    }
    varNames.forEach(function (name) {
      const use = new RegExp('\\b' + name + '\\b', 'g');
      let hit = use.exec(masked);
      while (hit !== null) {
        push(hit.index, hit.index + name.length, 'variable');
        hit = use.exec(masked);
      }
    });
    return ranges;
  }
  // ---- 函数名识别（纯文本）结束 ----

  const MAX_SEM_CHARS = 1200000;
  const semDecorations = editor.createDecorationsCollection();
  let semTimer = 0;
  let semRetry = 0;
  let lastSemModel = null;

  function scheduleCppSemantics(delay) {
    if (semTimer) { clearTimeout(semTimer); }
    semTimer = setTimeout(function () {
      semTimer = 0;
      applyCppSemantics();
    }, typeof delay === 'number' ? delay : 260);
  }

  function collectIncludedHeaders(model) {
    const headers = new Set();
    const count = model.getLineCount();
    for (let line = 1; line <= count; line += 1) {
      const text = model.getLineContent(line);
      if (text.indexOf('#') === -1) { continue; }
      const match = /^[ \t]*#[ \t]*include[ \t]*[<"]([^>"]+)[>"]/.exec(text);
      if (!match) { continue; }
      const full = match[1].trim();
      // bits/stdc++.h 这类万能头文件把标准库全带进来了，视为全部已包含
      if (full === 'bits/stdc++.h' || full.indexOf('bits/') === 0) { headers.add('*'); }
      // 带子目录的头文件（sys/types.h）只按文件名判断
      headers.add(full.substring(full.lastIndexOf('/') + 1));
    }
    return headers;
  }

  function applyCppSemantics() {
    const model = editor.getModel();
    const tab = activeTab();

    if (lastSemModel && lastSemModel !== model && !lastSemModel.isDisposed()) {
      monaco.editor.setModelMarkers(lastSemModel, 'mny-cpp', []);
    }
    lastSemModel = null;
    semDecorations.clear();

    if (!model || !tab || tab.largeFile || model.getLanguageId() !== 'cpp') { return; }
    if (model.getValueLength() > MAX_SEM_CHARS) { return; }

    const text = model.getValue();
    const tokenized = monaco.editor.tokenize(text, 'cpp');
    if (!tokenized || tokenized.length === 0) { return; }

    // 语言包可能还没加载完，此时拿不到有效 token；稍后重试几次
    let hasTokens = false;
    for (let i = 0; i < tokenized.length && !hasTokens; i += 1) {
      const tokens = tokenized[i] || [];
      for (let k = 0; k < tokens.length; k += 1) {
        if (tokens[k].type) { hasTokens = true; break; }
      }
    }
    if (!hasTokens && model.getValueLength() > 0 && semRetry < 3) {
      semRetry += 1;
      scheduleCppSemantics(200);
      return;
    }
    semRetry = 0;

    // 展平成 token 列表，便于判断前后文
    const flat = [];
    for (let i = 0; i < tokenized.length; i += 1) {
      const tokens = tokenized[i] || [];
      if (tokens.length === 0) { continue; }
      const lineText = model.getLineContent(i + 1);
      for (let k = 0; k < tokens.length; k += 1) {
        const start = tokenOffset(tokens[k]);
        if (typeof start !== 'number') { continue; }
        const rawEnd = k + 1 < tokens.length ? tokenOffset(tokens[k + 1]) : lineText.length;
        const end = typeof rawEnd === 'number' ? rawEnd : lineText.length;
        const word = lineText.slice(start, end);
        if (!word.trim()) { continue; }
        flat.push({
          line: i + 1, start: start, end: end, word: word,
          type: tokens[k].type || ''
        });
      }
    }

    // 第一遍：收集本文件里声明的类型名（class/struct/enum/union/using 后面的名字）
    const userTypes = new Set();
    for (let i = 0; i < flat.length; i += 1) {
      const token = flat[i];
      if (token.type.indexOf('identifier') !== 0) { continue; }
      const prev = previousMeaningfulToken(flat, i);
      if (prev && /^keyword\.(class|struct|enum|union|using)\b/.test(prev.type)) {
        userTypes.add(token.word);
      }
    }

    const headers = collectIncludedHeaders(model);
    const decorations = [];
    const markers = [];

    for (let i = 0; i < flat.length; i += 1) {
      const token = flat[i];
      // 处理标识符和关键字（跳过字符串、注释、预处理指令等）
      const isIdentifier = token.type.indexOf('identifier') === 0;
      const isKeyword = token.type.indexOf('keyword') === 0;
      if (!isIdentifier && !isKeyword) { continue; }
      const word = token.word;

      let className = '';
      const lib = CPP_SYMBOL_INFO.get(word);

      if (isKeyword) {
        // 基础类型用类型色，其他关键字保持主题的紫色
        if (CPP_TYPE_WORDS.has(word)) { className = 'mny-sem-type'; }
      } else if (CPP_EXTRA_KEYWORDS.has(word)) {
        className = 'mny-sem-keyword';
      } else if (word === 'std') {
        className = 'mny-sem-type';
      } else if (userTypes.has(word)) {
        // 本文件里声明的类型优先，避免和库里的同名符号冲突
        className = 'mny-sem-type';
      } else if (lib) {
        // 库符号必须 include 了对应头文件才着色
        if (libHeaderIncluded(headers, lib)) {
          className = lib.type ? 'mny-sem-type' : 'mny-sem-function';
        } else if (CPP_CHECK_SYMBOLS.has(word) && markers.length < 100) {
          markers.push({
            severity: monaco.MarkerSeverity.Warning,
            message: word + ' 定义在 <' + lib.headers[0] + '> 中，当前文件没有包含这个头文件',
            startLineNumber: token.line, startColumn: token.start + 1,
            endLineNumber: token.line, endColumn: token.end + 1
          });
        }
      }

      if (!className) { continue; }
      decorations.push({
        range: new monaco.Range(token.line, token.start + 1, token.line, token.end + 1),
        options: { inlineClassName: className }
      });
    }

    // 函数名和变量名用纯文本规则识别，和语法分词无关
    const lineStarts = [0];
    for (let i = 0; i < text.length; i += 1) {
      if (text[i] === '\n') { lineStarts.push(i + 1); }
    }
    const seen = new Set();
    const symbols = collectTextSymbols(text);
    for (let i = 0; i < symbols.length; i += 1) {
      const symbol = symbols[i];
      const from = offsetToPosition(lineStarts, symbol.start);
      const to = offsetToPosition(lineStarts, symbol.end);
      const key = from.line + ':' + from.column;
      if (seen.has(key)) { continue; }
      seen.add(key);
      decorations.push({
        range: new monaco.Range(from.line, from.column, to.line, to.column),
        options: { inlineClassName: symbol.kind === 'function' ? 'mny-sem-function' : 'mny-sem-variable' }
      });
    }

    semDecorations.set(decorations);
    monaco.editor.setModelMarkers(model, 'mny-cpp', markers);
    lastSemModel = model;
  }

  editor.onDidChangeModelContent(function () {
    scheduleCppSemantics();
    scheduleOutline();
  });

  function post(message) {
    if (host) { host.postMessage(message); }
  }

  /* ---------- 状态栏：光标行列 + 保存编码 ---------- */

  const cursorPosEl = document.getElementById('cursorPos');
  const encodingSelect = document.getElementById('encodingSelect');
  const ENCODING_LABELS = { utf8: 'UTF-8', utf8bom: 'UTF-8 BOM', gbk: 'GBK' };
  let saveEncoding = 'utf8';

  function updateCursorPos() {
    const pos = editor.getPosition();
    if (!cursorPosEl || !pos) { return; }
    cursorPosEl.textContent = '行 ' + pos.lineNumber + '，列 ' + pos.column;
  }

  function applyEncoding(value) {
    if (!encodingSelect) { return; }
    saveEncoding = ENCODING_LABELS[value] ? value : 'utf8';
    encodingSelect.value = saveEncoding;
  }

  editor.onDidChangeCursorPosition(updateCursorPos);
  editor.onDidChangeModel(updateCursorPos);
  if (encodingSelect) {
    encodingSelect.addEventListener('change', function () {
      const value = encodingSelect.value;
      applyEncoding(value);
      post({ type: 'setEncoding', value: value });
    });
  }
  updateCursorPos();

  /* ---------- 标签管理 ---------- */

  const tabs = [];
  let activeId = null;
  let untitledSeq = 0;
  let newFileTemplate = '';

  function activeTab() {
    for (const tab of tabs) {
      if (tab.id === activeId) { return tab; }
    }
    return null;
  }

  function renderTabs() {
    tabsEl.innerHTML = '';
    for (const tab of tabs) {
      const el = document.createElement('div');
      el.className = 'tab' + (tab.id === activeId ? ' active' : '');
      el.title = tab.path || (tab.name + '（尚未保存）');

      const nameEl = document.createElement('span');
      nameEl.className = 'name';
      nameEl.textContent = tab.name;
      el.appendChild(nameEl);

      if (tab.dirty) {
        const dot = document.createElement('span');
        dot.className = 'dot';
        dot.textContent = '●';
        el.appendChild(dot);
      }

      const closeEl = document.createElement('span');
      closeEl.className = 'close';
      closeEl.textContent = '×';
      closeEl.title = '关闭';
      closeEl.addEventListener('click', function (ev) {
        ev.stopPropagation();
        closeTab(tab);
      });
      el.appendChild(closeEl);

      el.addEventListener('click', function () { activate(tab.id); });
      el.addEventListener('mousedown', function (ev) {
        if (ev.button === 1) { ev.preventDefault(); closeTab(tab); }
      });

      tabsEl.appendChild(el);
    }

    // 标签栏末尾的新建按钮，等同 Ctrl+N
    const addEl = document.createElement('button');
    addEl.type = 'button';
    addEl.className = 'tab-add';
    addEl.textContent = '＋';
    addEl.title = '新建文件（Ctrl+N）';
    addEl.addEventListener('click', function () { newUntitled(); });
    tabsEl.appendChild(addEl);
  }

  /* ---------- 编辑器选项（由宿主设置驱动） ---------- */

  function baseEditorOptions() {
    return {
      fontSize: settings.fontSize,
      fontFamily: settings.fontFamily,
      fontLigatures: settings.fontLigatures,
      wordWrap: settings.wordWrap,
      minimap: { enabled: settings.minimap },
      renderWhitespace: settings.whitespace,
      cursorStyle: settings.cursorStyle,
      cursorBlinking: settings.cursorBlinking,
      tabSize: settings.tabSize,
      insertSpaces: settings.insertSpaces,
      folding: true,
      largeFileOptimizations: true,
      quickSuggestions: settings.suggest
        ? { other: true, comments: false, strings: false }
        : false,
      suggestOnTriggerCharacters: settings.suggest,
      wordBasedSuggestions: settings.suggest ? true : 'off',
      parameterHints: { enabled: settings.parameterHints },
      hover: { enabled: settings.hover },
      suggest: { showKeywords: settings.suggest, showSnippets: settings.suggest },
      occurrencesHighlight: settings.occurrencesHighlight ? 'singleFile' : 'off',
      selectionHighlight: true,
      matchBrackets: 'always',
      bracketPairColorization: { enabled: settings.bracketColorization },
      guides: { bracketPairs: false, indentation: true },
      codeLens: true,
      colorDecorators: true,
      links: true,
      stickyScroll: { enabled: settings.stickyScroll },
      renderValidationDecorations: 'editable'
    };
  }

  function applyFileMode(tab) {
    const large = !!(tab && tab.largeFile);
    largeFileNoticeEl.classList.toggle('visible', large);

    const base = baseEditorOptions();
    if (!large) {
      editor.updateOptions(base);
      return;
    }

    // 大文件模式：关掉补全、悬浮、括号着色等重型特性
    editor.updateOptions(Object.assign({}, base, {
      wordWrap: 'off',
      minimap: { enabled: false },
      folding: false,
      quickSuggestions: false,
      suggestOnTriggerCharacters: false,
      wordBasedSuggestions: 'off',
      parameterHints: { enabled: false },
      hover: { enabled: false },
      occurrencesHighlight: 'off',
      selectionHighlight: false,
      matchBrackets: 'never',
      bracketPairColorization: { enabled: false },
      guides: { bracketPairs: false, indentation: false },
      codeLens: false,
      colorDecorators: false,
      links: false,
      stickyScroll: { enabled: false },
      renderWhitespace: 'none',
      renderValidationDecorations: 'off',
      suggest: { showKeywords: false, showSnippets: false }
    }));
  }

  /* 把宿主持有的设置套用到编辑器（设置页改完立即生效） */
  function applyEditorOptions() {
    applyTheme();
    applyUiZoom(settings.uiZoom);
    applyFileMode(activeTab());
  }

  /* 编辑器内的交互改了设置，回传给宿主，宿主再存盘并同步设置页 */
  function reportState() {
    post({
      type: 'editorStateChanged',
      wordWrap: settings.wordWrap,
      minimap: settings.minimap,
      fontSize: settings.fontSize,
      uiZoom: settings.uiZoom
    });
  }

  function activate(id) {
    clearViewportHighlight();
    if (activeId !== null) {
      const current = activeTab();
      if (current && !current.model.isDisposed()) {
        current.viewState = editor.saveViewState();
      }
    }

    activeId = id;
    const tab = activeTab();
    applyFileMode(tab);
    // 切换标签后刷新代码大纲（如果大纲栏目正打开着）
    scheduleOutline();
    // 切换标签后重新做一遍语义着色（语法包可能刚加载，所以留一点延迟）
    scheduleCppSemantics(120);

    if (!tab || tab.model.isDisposed()) {
      editor.setModel(null);
      renderTabs();
      return;
    }

    editor.setModel(tab.model);
    if (tab.viewState) { editor.restoreViewState(tab.viewState); }
    editor.focus();
    renderTabs();
    notifyTabs();
  }

  function registerTab(model, name, path, largeFile) {
    const tab = {
      id: model.id, name: name, path: path, model: model,
      viewState: null, dirty: false, largeFile: !!largeFile
    };
    model.onDidChangeContent(function () {
      if (!tab.dirty) {
        tab.dirty = true;
        renderTabs();
        notifyTabs();
      }
    });
    tabs.push(tab);
    activate(tab.id);
    notifyTabs();
    return tab;
  }

  function notifyTabs() {
    const tab = activeTab();
    post({
      type: 'tabsChanged',
      count: tabs.length,
      name: tab ? tab.name : null,
      hasUnsaved: tabs.some(function (t) { return t.dirty; })
    });
  }

  function newUntitled() {
    untitledSeq += 1;
    const name = '未命名-' + untitledSeq + '.cpp';
    const model = monaco.editor.createModel(newFileTemplate, 'cpp', monaco.Uri.parse('inmemory://mny/untitled/' + untitledSeq));
    registerTab(model, name, null, false);
  }

  function openFile(path, name, content, largeFile) {
    for (const tab of tabs) {
      if (tab.path === path) { activate(tab.id); return; }
    }
    const uri = monaco.Uri.file(path);
    const language = largeFile ? 'plaintext' : languageFor(name);
    const model = monaco.editor.createModel(content, language, uri);
    registerTab(model, name, path, largeFile);
  }

  /* ---------- 分块接收（大文件单条消息发不过来） ---------- */
  let incomingFile = null;

  function beginIncomingFile(path, name, largeFile) {
    incomingFile = { path: path, name: name, largeFile: !!largeFile, chunks: [] };
  }

  function appendIncomingChunk(text) {
    if (incomingFile) { incomingFile.chunks.push(text); }
  }

  function endIncomingFile() {
    if (!incomingFile) { return; }
    const file = incomingFile;
    incomingFile = null;
    openFile(file.path, file.name, file.chunks.join(''), file.largeFile);
  }

  /* ---------- 大文件：Alt+F 只为可视区域及上下 5 行做着色 ---------- */
  const viewportDecorations = editor.createDecorationsCollection();
  const MAX_HIGHLIGHT_CHARS = 200000;
  const MAX_HIGHLIGHT_LINE_CHARS = 4000;
  let viewportHighlightActive = false;
  let viewportHighlightFrame = 0;
  let cppTokenizerReady = false;
  let cppTokenizerLoading = false;

  function ensureCppTokenizer() {
    if (cppTokenizerReady || cppTokenizerLoading) { return; }
    cppTokenizerLoading = true;
    // 大文件从不用 cpp 模型，基本语言包可能尚未加载；显式加载后才能得到彩色 token。
    require(['vs/basic-languages/cpp/cpp'], function (module) {
      monaco.languages.setMonarchTokensProvider('cpp', module.language);
      cppTokenizerReady = true;
      cppTokenizerLoading = false;
      if (viewportHighlightActive) { scheduleViewportHighlight(); }
    });
  }

  function clearViewportHighlight() {
    viewportHighlightActive = false;
    if (viewportHighlightFrame) {
      cancelAnimationFrame(viewportHighlightFrame);
      viewportHighlightFrame = 0;
    }
    viewportDecorations.clear();
  }

  function viewportTokenClass(type) {
    if (!type) { return ''; }
    if (type.indexOf('comment') === 0) { return 'mny-vh-comment'; }
    if (type.indexOf('string.include') === 0) { return 'mny-vh-include'; }
    if (type.indexOf('string.escape') === 0) { return 'mny-vh-escape'; }
    if (type.indexOf('string') === 0) { return 'mny-vh-string'; }
    if (type.indexOf('keyword') === 0) { return 'mny-vh-keyword'; }
    if (type.indexOf('number') === 0) { return 'mny-vh-number'; }
    if (type.indexOf('type') === 0) { return 'mny-vh-type'; }
    if (type.indexOf('operator') === 0) { return 'mny-vh-operator'; }
    if (type.indexOf('delimiter') === 0) { return 'mny-vh-delimiter'; }
    return '';
  }

  function applyViewportHighlight() {
    const tab = activeTab();
    const model = editor.getModel();
    if (!viewportHighlightActive || !tab || !tab.largeFile || !model) {
      viewportDecorations.clear();
      return;
    }
    if (!cppTokenizerReady) {
      ensureCppTokenizer();
      return;
    }

    const ranges = editor.getVisibleRanges();
    if (ranges.length === 0) { return; }
    let startLine = ranges[0].startLineNumber;
    let endLine = ranges[0].endLineNumber;
    for (const range of ranges) {
      startLine = Math.min(startLine, range.startLineNumber);
      endLine = Math.max(endLine, range.endLineNumber);
    }
    startLine = Math.max(1, startLine - 5);
    endLine = Math.min(model.getLineCount(), endLine + 5);

    const lines = [];
    let totalChars = 0;
    for (let line = startLine; line <= endLine; line += 1) {
      const text = model.getLineContent(line).slice(0, MAX_HIGHLIGHT_LINE_CHARS);
      if (totalChars + text.length > MAX_HIGHLIGHT_CHARS) { break; }
      lines.push(text);
      totalChars += text.length + 1;
    }
    if (lines.length === 0) { return; }

    // 只对截取的局部文本分词，绝不把模型切换回 C++ 全文高亮。
    const tokenized = monaco.editor.tokenize(lines.join('\n'), 'cpp');
    const decorations = [];
    for (let lineIndex = 0; lineIndex < lines.length; lineIndex += 1) {
      const tokens = tokenized[lineIndex] || [];
      for (let tokenIndex = 0; tokenIndex < tokens.length; tokenIndex += 1) {
        const className = viewportTokenClass(tokens[tokenIndex].type);
        if (!className) { continue; }
        // Monaco 0.52 使用 offset；保留 startIndex 以兼容旧版本。
        const startIndex = typeof tokens[tokenIndex].offset === 'number'
          ? tokens[tokenIndex].offset
          : tokens[tokenIndex].startIndex;
        if (typeof startIndex !== 'number') { continue; }
        const nextToken = tokenIndex + 1 < tokens.length ? tokens[tokenIndex + 1] : null;
        const nextStartIndex = nextToken
          ? (typeof nextToken.offset === 'number' ? nextToken.offset : nextToken.startIndex)
          : lines[lineIndex].length;
        if (typeof nextStartIndex !== 'number') { continue; }
        const startColumn = startIndex + 1;
        const endColumn = nextStartIndex + 1;
        if (endColumn <= startColumn) { continue; }
        decorations.push({
          range: new monaco.Range(startLine + lineIndex, startColumn, startLine + lineIndex, endColumn),
          options: { inlineClassName: className }
        });
      }
    }
    viewportDecorations.set(decorations);
  }

  function scheduleViewportHighlight() {
    if (!viewportHighlightActive || viewportHighlightFrame) { return; }
    viewportHighlightFrame = requestAnimationFrame(function () {
      viewportHighlightFrame = 0;
      applyViewportHighlight();
    });
  }

  editor.onKeyDown(function (e) {
    const tab = activeTab();
    if (!tab || !tab.largeFile || !e.altKey || e.ctrlKey || e.metaKey ||
        e.keyCode !== monaco.KeyCode.KeyF) { return; }
    e.preventDefault();
    e.stopPropagation();
    viewportHighlightActive = true;
    scheduleViewportHighlight();
  });
  editor.onKeyUp(function (e) {
    if (e.keyCode === monaco.KeyCode.KeyF || !e.altKey) { clearViewportHighlight(); }
  });
  editor.onDidScrollChange(function () {
    if (viewportHighlightActive) { scheduleViewportHighlight(); }
  });
  editor.onDidChangeModelContent(function () {
    if (viewportHighlightActive) { scheduleViewportHighlight(); }
  });
  editor.onDidBlurEditorText(clearViewportHighlight);
  window.addEventListener('keyup', function (e) {
    if (e.key === 'Alt' || (e.key && e.key.toLowerCase() === 'f')) { clearViewportHighlight(); }
  });
  window.addEventListener('blur', clearViewportHighlight);

  function closeTab(tab) {
    const index = tabs.indexOf(tab);
    if (index < 0) { return; }

    clearViewportHighlight();
    tabs.splice(index, 1);
    if (activeId === tab.id) { activeId = null; }
    if (editor.getModel() === tab.model) { editor.setModel(null); }
    tab.model.dispose();

    if (activeId === null) {
      const next = tabs[Math.min(index, tabs.length - 1)];
      if (next) { activate(next.id); } else { applyFileMode(null); renderTabs(); }
    } else {
      renderTabs();
    }
    notifyTabs();
  }

  /* ---------- 批量保存（关闭窗口前保存所有脏标签） ---------- */

  let saveAllQueue = null;

  function saveAll() {
    if (saveAllQueue !== null) { return; }
    const dirty = tabs.filter(function (t) { return t.dirty; });
    if (dirty.length === 0) {
      post({ type: 'saveAllDone', cancelled: false });
      return;
    }
    saveAllQueue = dirty;
    processSaveAllNext();
  }

  function processSaveAllNext() {
    while (saveAllQueue !== null && saveAllQueue.length > 0) {
      const tab = saveAllQueue.shift();
      if (!tab.dirty) { continue; }
      post({
        type: tab.path ? 'saveRequest' : 'saveAsRequest',
        path: tab.path,
        name: tab.name,
        content: tab.model.getValue()
      });
      return;
    }
    const cancelled = tabs.some(function (t) { return t.dirty; });
    saveAllQueue = null;
    post({ type: 'saveAllDone', cancelled: cancelled });
  }

  /* ---------- 保存 ---------- */

  function requestSave(saveAs) {
    const tab = activeTab();
    if (!tab) { return; }
    post({
      type: saveAs ? 'saveAsRequest' : 'saveRequest',
      path: tab.path,
      name: tab.name,
      content: tab.model.getValue()
    });
  }

  function markSaved(path, name) {
    let tab = activeTab();
    for (const candidate of tabs) {
      if (candidate.path === path) { tab = candidate; break; }
    }
    if (!tab) { return; }
    const wasUnsaved = !tab.path;
    tab.path = path;
    tab.name = name;
    tab.dirty = false;
    renderTabs();
    notifyTabs();

    // 未保存时点的断点之前没有路径，保存后补登记
    const savedBreakpoints = breakpointLines.get(tab.model.id);
    if (wasUnsaved && savedBreakpoints) {
      savedBreakpoints.forEach(function (line) {
        post({ type: 'breakpointToggle', path: tab.path, line: line, enabled: true });
      });
    }

    // 批量保存中：继续保存下一个脏标签
    if (saveAllQueue !== null) {
      processSaveAllNext();
      return;
    }

    // 保存完成后继续之前被拦截的检查/调试请求
    if (pendingFileAction) {
      const action = pendingFileAction;
      pendingFileAction = null;
      post({
        type: action === 'check' ? 'checkRequest' : 'debugStartRequest',
        path: tab.path,
        name: tab.name
      });
      return;
    }

    // 保存完成后继续之前被拦截的编译/运行请求
    if (pendingBuildKind) {
      const kind = pendingBuildKind;
      pendingBuildKind = null;
      post({ type: 'buildRequest', kind: kind, path: tab.path, name: tab.name });
    }
  }

  /* ---------- 编译 / 运行 ---------- */

  let pendingBuildKind = null;

  function requestBuild(kind) {
    const tab = activeTab();
    if (!tab) {
      post({ type: 'hostMessage', text: '当前没有打开的文件。' });
      return;
    }
    // 还没保存过、或有未保存的修改：先弹保存对话框，保存完成后自动继续编译/运行
    if (!tab.path || tab.dirty) {
      pendingBuildKind = kind;
      requestSave(false);
      return;
    }
    post({ type: 'buildRequest', kind: kind, path: tab.path, name: tab.name });
  }

  /* ---------- 操作 ---------- */

  function runAction(id) {
    if (!id) { return; }
    const action = editor.getAction(id);
    if (action) { action.run(); return; }
    editor.trigger('mny', id, null);
  }

  function toggleWordWrap() {
    const tab = activeTab();
    if (tab && tab.largeFile) { return; }
    settings.wordWrap = settings.wordWrap === 'off' ? 'on' : 'off';
    editor.updateOptions({ wordWrap: settings.wordWrap });
    writeSettings();
    reportState();
  }

  function toggleMinimap() {
    const tab = activeTab();
    if (tab && tab.largeFile) { return; }
    settings.minimap = !settings.minimap;
    editor.updateOptions({ minimap: { enabled: settings.minimap } });
    writeSettings();
    reportState();
  }

  function changeFontSize(delta) {
    const size = delta === 0 ? 13 : Math.min(40, Math.max(8, settings.fontSize + delta));
    settings.fontSize = size;
    editor.updateOptions({ fontSize: size });
    writeSettings();
    reportState();
  }

  /* Ctrl + '+' / '-'：缩放整个编辑器界面（标签栏、终端、编辑器一起放大缩小） */
  function applyUiZoom(zoom) {
    document.documentElement.style.zoom = zoom;
    editor.layout();
  }

  function changeUiZoom(delta) {
    const zoom = delta === 0
      ? 1
      : Math.min(2, Math.max(0.5, Math.round((settings.uiZoom + delta * 0.1) * 100) / 100));
    settings.uiZoom = zoom;
    applyUiZoom(zoom);
    writeSettings();
    reportState();
  }

  function handleCommand(name) {
    switch (name) {
      case 'newUntitled': newUntitled(); break;
      case 'closeActive': {
        const tab = activeTab();
        if (tab) { closeTab(tab); }
        break;
      }
      case 'requestSave': requestSave(false); break;
      case 'saveAs': requestSave(true); break;
      case 'toggleWordWrap': toggleWordWrap(); break;
      case 'toggleMinimap': toggleMinimap(); break;
      case 'fontSizeUp': changeFontSize(1); break;
      case 'fontSizeDown': changeFontSize(-1); break;
      case 'fontSizeReset': changeFontSize(0); break;
      case 'focus': editor.focus(); break;
      case 'toggleTerminal': toggleTerminal(); break;
      case 'newTerminal':
        panelEl.classList.remove('collapsed');
        createTerminal();
        break;
      case 'clearTerminal': clearActiveTerminal(); break;
      case 'closeAllTerminals': closeAllTerminals(); break;
      case 'hideTerminalPanel': hidePanel(); break;
      case 'build': requestBuild('compile'); break;
      case 'run': requestBuild('run'); break;
      case 'buildRun': requestBuild('buildRun'); break;
      case 'check': requestFileAction('check'); break;
      case 'debugStart': requestFileAction('debugStart'); break;
      case 'saveAll': saveAll(); break;
    }
  }

  /* ---------- 调试：断点 / 当前执行行 ---------- */

  const breakpointLines = new Map();          // modelId -> Set<line>
  const breakpointDecorationIds = new Map();  // modelId -> decoration ids

  function modelForPath(path) {
    if (!path) { return editor.getModel(); }
    for (const tab of tabs) {
      if (tab.path === path) { return tab.model; }
    }
    return null;
  }

  function renderBreakpoints(model) {
    if (!model || model.isDisposed()) { return; }
    const lines = breakpointLines.get(model.id);
    const decorations = [];
    if (lines) {
      lines.forEach(function (line) {
        decorations.push({
          range: new monaco.Range(line, 1, line, 1),
          options: { glyphMarginClassName: 'mny-breakpoint' }
        });
      });
    }
    const previous = breakpointDecorationIds.get(model.id) || [];
    breakpointDecorationIds.set(model.id, model.deltaDecorations(previous, decorations));
  }

  function toggleBreakpoint(model, line) {
    if (!model || !line) { return; }
    let lines = breakpointLines.get(model.id);
    if (!lines) { lines = new Set(); breakpointLines.set(model.id, lines); }
    let enabled;
    if (lines.has(line)) { lines.delete(line); enabled = false; }
    else { lines.add(line); enabled = true; }
    renderBreakpoints(model);

    const tab = tabs.find(function (t) { return t.model === model; });
    post({ type: 'breakpointToggle', path: tab ? tab.path : null, line: line, enabled: enabled });
  }

  /* 宿主打开文件时回传该文件已有的断点 */
  function applyBreakpointList(path, lines) {
    const model = modelForPath(path);
    if (!model) { return; }
    breakpointLines.set(model.id, new Set(lines || []));
    renderBreakpoints(model);
  }

  editor.onMouseDown(function (e) {
    const type = e.target && e.target.type;
    if (type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN) { return; }
    const line = (e.target.position && e.target.position.lineNumber) ||
      (e.target.range && e.target.range.startLineNumber);
    if (line) { toggleBreakpoint(editor.getModel(), line); }
  });

  let debugLineModel = null;
  let debugLineIds = [];

  function clearDebugLine() {
    if (debugLineModel && !debugLineModel.isDisposed()) {
      debugLineModel.deltaDecorations(debugLineIds, []);
    }
    debugLineModel = null;
    debugLineIds = [];
  }

  function showDebugLine(path, line) {
    clearDebugLine();
    const model = modelForPath(path);
    if (!model || !line) { return; }
    debugLineModel = model;
    debugLineIds = model.deltaDecorations([], [{
      range: new monaco.Range(line, 1, line, 1),
      options: {
        isWholeLine: true,
        className: 'mny-debug-line',
        glyphMarginClassName: 'mny-debug-line-glyph'
      }
    }]);
    if (editor.getModel() === model) {
      editor.revealLineInCenterIfOutsideViewport(line);
    }
  }

  /* ---------- 代码检查器：诊断波浪线 ---------- */

  const CHECK_OWNER = 'mny-check';

  function clearDiagnostics(path) {
    const model = modelForPath(path);
    if (model) { monaco.editor.setModelMarkers(model, CHECK_OWNER, []); }
  }

  function applyDiagnostics(path, items) {
    const model = modelForPath(path);
    if (!model) { return; }
    const markers = (items || []).map(function (item) {
      const line = Math.max(1, item.line || 1);
      const column = Math.max(1, item.column || 1);
      return {
        severity: item.severity === 'warning' ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Error,
        message: item.message || '',
        startLineNumber: line,
        startColumn: column,
        endLineNumber: item.endLine || line,
        endColumn: item.endColumn || column + 1
      };
    });
    monaco.editor.setModelMarkers(model, CHECK_OWNER, markers);
  }

  function revealPosition(line, column) {
    const model = editor.getModel();
    if (!model || !line) { return; }
    const target = {
      lineNumber: Math.min(model.getLineCount(), Math.max(1, line)),
      column: Math.max(1, column || 1)
    };
    editor.setPosition(target);
    editor.revealPositionInCenter(target);
    editor.focus();
  }

  /* ---------- 检查 / 调试：确保文件已保存后再请求宿主 ---------- */

  let pendingFileAction = null;

  function requestFileAction(kind) {
    const tab = activeTab();
    if (!tab) {
      // 没有打开文件时交给宿主在面板里提示，不弹对话框打断输入
      post({
        type: kind === 'check' ? 'checkRequest' : 'debugStartRequest',
        path: '',
        name: null
      });
      return;
    }
    if (!tab.path || tab.dirty) {
      pendingFileAction = kind;
      requestSave(false);
      return;
    }
    post({
      type: kind === 'check' ? 'checkRequest' : 'debugStartRequest',
      path: tab.path,
      name: tab.name
    });
  }

  /* ---------- 代码大纲 ---------- */

  let outlineWanted = false;
  let outlineTimer = null;

  function requestOutline() {
    outlineWanted = true;
    sendOutline();
  }

  function stopOutline() {
    outlineWanted = false;
    if (outlineTimer) { clearTimeout(outlineTimer); outlineTimer = null; }
  }

  function sendOutline() {
    const model = editor.getModel();
    if (!model) {
      post({ type: 'outline', path: null, name: null, symbols: [] });
      return;
    }
    const tab = activeTab();
    let symbols = [];
    try { symbols = collectOutlineSymbols(model.getValue()); } catch (e) { symbols = []; }
    post({
      type: 'outline',
      path: tab ? tab.path : null,
      name: tab ? tab.name : null,
      symbols: symbols
    });
  }

  function scheduleOutline() {
    if (!outlineWanted) { return; }
    if (outlineTimer) { clearTimeout(outlineTimer); }
    outlineTimer = setTimeout(sendOutline, 400);
  }

  const OUTLINE_MAX_CHARS = 200000;
  const OUTLINE_CONTROL_WORDS = new Set([
    'if', 'for', 'while', 'switch', 'catch', 'return', 'else', 'do', 'sizeof',
    'new', 'delete', 'throw', 'case', 'default'
  ]);

  /* 把注释和字符串替换成空格，长度和换行保持不变，方便按行统计花括号层级 */
  function stripCommentsAndStrings(text) {
    const chars = text.split('');
    let i = 0;
    while (i < chars.length) {
      const c = chars[i];
      if (c === '/' && chars[i + 1] === '/') {
        while (i < chars.length && chars[i] !== '\n') { chars[i] = ' '; i += 1; }
      } else if (c === '/' && chars[i + 1] === '*') {
        chars[i] = ' '; chars[i + 1] = ' '; i += 2;
        while (i < chars.length && !(chars[i] === '*' && chars[i + 1] === '/')) {
          if (chars[i] !== '\n') { chars[i] = ' '; }
          i += 1;
        }
        if (i < chars.length) { chars[i] = ' '; chars[i + 1] = ' '; i += 2; }
      } else if (c === '"' || c === "'") {
        const quote = c;
        chars[i] = ' '; i += 1;
        while (i < chars.length && chars[i] !== quote) {
          if (chars[i] === '\\') {
            chars[i] = ' '; i += 1;
            if (i < chars.length) { chars[i] = ' '; i += 1; }
            continue;
          }
          if (chars[i] !== '\n') { chars[i] = ' '; }
          i += 1;
        }
        if (i < chars.length) { chars[i] = ' '; i += 1; }
      } else {
        i += 1;
      }
    }
    return chars.join('');
  }

  /* 轻量 C/C++ 大纲：命名空间 / 类 / 结构体 / 联合体 / 枚举 / 函数 / 宏 */
  function collectOutlineSymbols(text) {
    if (!text) { return []; }
    const source = text.length > OUTLINE_MAX_CHARS ? text.slice(0, OUTLINE_MAX_CHARS) : text;
    const lines = source.split('\n');
    const cleanLines = stripCommentsAndStrings(source).split('\n');
    const root = { name: '', kind: 'root', line: 0, column: 1, children: [] };
    const stack = [{ node: root, depth: -1 }];
    const typeNames = new Set();
    let depth = 0;

    // 当前是否在类/结构体内部（用于识别只有声明、没有函数体的成员函数）
    function insideType() {
      for (let s = stack.length - 1; s >= 1; s -= 1) {
        const kind = stack[s].node.kind;
        if (kind === 'class' || kind === 'struct' || kind === 'union') { return true; }
        if (kind === 'namespace') { return false; }
      }
      return false;
    }

    for (let i = 0; i < lines.length; i += 1) {
      const raw = lines[i];
      const trimmed = raw.trim();
      let found = null;

      if (trimmed.indexOf('#') === 0) {
        const defineMatch = /^#\s*define\s+([A-Za-z_]\w*)/.exec(trimmed);
        if (defineMatch) { found = { kind: 'macro', name: defineMatch[1] }; }
      } else if (trimmed.length > 0) {
        let match = /^\s*namespace\s+([A-Za-z_][\w:]*)/.exec(raw);
        if (match) {
          found = { kind: 'namespace', name: match[1] };
        }
        if (!found) {
          match = /^\s*(?:typedef\s+)?(class|struct|union|enum(?:\s+class)?)\s+([A-Za-z_]\w*)/.exec(raw);
          if (match) {
            found = { kind: match[1].split(' ')[0], name: match[2] };
            typeNames.add(match[2]);
          }
        }
        if (!found) {
          match = /^\s*([A-Za-z_~][\w:<>,\s\*&]*?)\s+([A-Za-z_]\w*(?:::\w+)*)\s*\(([^;{}]*)\)\s*(?:const\s*)?(?:[;{]|$)/.exec(raw);
          if (match) {
            const returnType = match[1].trim();
            const name = match[2];
            const declarationOnly = trimmed.endsWith(';') && !insideType();
            if (returnType && !declarationOnly &&
                !OUTLINE_CONTROL_WORDS.has(name) &&
                returnType.indexOf('=') < 0 &&
                !/\b(if|for|while|switch|catch|return)\b/.test(returnType)) {
              found = { kind: 'function', name: name };
            }
          }
          if (!found && insideType()) {
            const ctor = /^\s*([A-Za-z_]\w*)\s*\(([^;{}]*)\)\s*(?::[^{;]*)?(?:[;{]|$)/.exec(raw);
            if (ctor && typeNames.has(ctor[1])) {
              found = { kind: 'function', name: ctor[1] };
            }
          }
        }
      }

      if (found) {
        while (stack.length > 1 && stack[stack.length - 1].depth >= depth) { stack.pop(); }
        const parent = stack[stack.length - 1].node;
        found.line = i + 1;
        found.column = Math.max(1, raw.search(/\S/) + 1);
        found.children = [];
        parent.children.push(found);
        stack.push({ node: found, depth: depth });
      }

      const cleanLine = cleanLines[i] || '';
      for (let k = 0; k < cleanLine.length; k += 1) {
        if (cleanLine[k] === '{') { depth += 1; }
        else if (cleanLine[k] === '}') { depth = Math.max(0, depth - 1); }
      }
    }

    return root.children;
  }

  /* ---------- 终端面板 ---------- */

  const panelEl = document.getElementById('panel');
  const termTabsEl = document.getElementById('termTabs');
  const termBodyEl = document.getElementById('termBody');
  const MAX_TERM_LINES = 2000;

  let terminalSeq = 0;
  const terminals = new Map();
  let activeTerminalId = null;

  function isPanelVisible() {
    return !panelEl.classList.contains('collapsed');
  }

  function showPanel() {
    if (isPanelVisible()) { focusActiveTerminal(); return; }
    panelEl.classList.remove('collapsed');
    if (terminals.size === 0) { createTerminal(); return; }
    activateTerminal(activeTerminalId);
  }

  function hidePanel() {
    panelEl.classList.add('collapsed');
    editor.focus();
  }

  function clearActiveTerminal() {
    const term = terminals.get(activeTerminalId);
    if (term) { term.outputEl.textContent = ''; }
  }

  function closeAllTerminals() {
    for (const id of Array.from(terminals.keys())) { killTerminal(id); }
  }

  function toggleTerminal() {
    if (isPanelVisible()) { hidePanel(); } else { showPanel(); }
  }

  function createTerminal() {
    terminalSeq += 1;
    const id = terminalSeq;

    const view = document.createElement('div');
    view.className = 'term-view';
    const output = document.createElement('pre');
    output.className = 'term-output';
    const inputRow = document.createElement('div');
    inputRow.className = 'term-input-row';
    const prompt = document.createElement('span');
    prompt.className = 'prompt';
    prompt.textContent = '❯';
    const input = document.createElement('input');
    input.className = 'term-input';
    input.spellcheck = false;
    input.setAttribute('autocomplete', 'off');
    inputRow.appendChild(prompt);
    inputRow.appendChild(input);
    view.appendChild(output);
    view.appendChild(inputRow);
    termBodyEl.appendChild(view);

    const term = { id: id, seq: terminalSeq, viewEl: view, outputEl: output, inputEl: input, tabEl: null };
    terminals.set(id, term);

    input.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter') { return; }
      const text = input.value;
      input.value = '';
      appendTerminalLine(term, '❯ ' + text);
      post({ type: 'terminalInput', id: id, text: text });
    });

    activateTerminal(id);
    post({ type: 'terminalCreate', id: id });
  }

  function renderTerminalTabs() {
    termTabsEl.innerHTML = '';
    for (const term of terminals.values()) {
      const tab = document.createElement('div');
      tab.className = 'term-tab' + (term.id === activeTerminalId ? ' active' : '');

      const nameEl = document.createElement('span');
      nameEl.className = 'tname';
      nameEl.textContent = 'cmd ' + term.seq;
      tab.appendChild(nameEl);

      const kill = document.createElement('span');
      kill.className = 'tkill';
      kill.textContent = '×';
      kill.title = '终止终端';
      kill.addEventListener('click', function (ev) {
        ev.stopPropagation();
        killTerminal(term.id);
      });
      tab.appendChild(kill);

      tab.addEventListener('click', function () { activateTerminal(term.id); });
      term.tabEl = tab;
      termTabsEl.appendChild(tab);
    }
  }

  function activateTerminal(id) {
    if (!terminals.has(id)) { return; }
    activeTerminalId = id;
    for (const term of terminals.values()) {
      term.viewEl.classList.toggle('active', term.id === id);
    }
    renderTerminalTabs();
    focusActiveTerminal();
  }

  function focusActiveTerminal() {
    const term = terminals.get(activeTerminalId);
    if (term) { term.inputEl.focus(); }
  }

  function killTerminal(id) {
    if (!terminals.has(id)) { return; }
    post({ type: 'terminalKill', id: id });
    removeTerminal(id);
  }

  function removeTerminal(id) {
    const term = terminals.get(id);
    if (!term) { return; }
    terminals.delete(id);
    term.viewEl.remove();

    if (terminals.size === 0) {
      activeTerminalId = null;
      renderTerminalTabs();
      hidePanel();
      return;
    }

    if (activeTerminalId === id) {
      activateTerminal(terminals.keys().next().value);
    } else {
      renderTerminalTabs();
    }
  }

  function appendTerminalLine(term, text) {
    const output = term.outputEl;
    output.appendChild(document.createTextNode(text + '\n'));
    while (output.childNodes.length > MAX_TERM_LINES) {
      output.removeChild(output.firstChild);
    }
    output.scrollTop = output.scrollHeight;
  }

  /// 在终端面板里追加一行提示（不执行任何命令）
  function terminalNote(text) {
    if (!isPanelVisible()) { panelEl.classList.remove('collapsed'); }
    if (terminals.size === 0) { createTerminal(); }
    let term = terminals.get(activeTerminalId) || terminals.values().next().value;
    if (!term) { return; }
    appendTerminalLine(term, text);
    focusActiveTerminal();
  }

  /// 追加一行编译输出（宿主直接运行 g++ 时逐行推送过来）
  function terminalStdout(text) {
    if (text === null || text === undefined) { return; }
    if (!isPanelVisible()) { panelEl.classList.remove('collapsed'); }
    if (terminals.size === 0) { createTerminal(); }
    let term = terminals.get(activeTerminalId) || terminals.values().next().value;
    if (!term) { return; }
    appendTerminalLine(term, text);
  }

  document.getElementById('termNew').addEventListener('click', function () { createTerminal(); });
  document.getElementById('termHide').addEventListener('click', function () { hidePanel(); });
  document.getElementById('termClose').addEventListener('click', function () {
    for (const id of Array.from(terminals.keys())) { killTerminal(id); }
  });

  /* ---------- 宿主消息 ---------- */

  if (host) {
    host.addEventListener('message', function (e) {
      const msg = e.data;
      if (!msg || typeof msg !== 'object') { return; }
      switch (msg.type) {
        case 'openFile': openFile(msg.path, msg.name, msg.content); break;
        case 'openFileBegin': beginIncomingFile(msg.path, msg.name, msg.largeFile); break;
        case 'openFileChunk': appendIncomingChunk(msg.text); break;
        case 'openFileEnd': endIncomingFile(); break;
        case 'action': runAction(msg.id); break;
        case 'command': handleCommand(msg.name); break;
        case 'saved': markSaved(msg.path, msg.name); break;
        case 'saveCancelled':
          if (saveAllQueue !== null) {
            saveAllQueue = null;
            post({ type: 'saveAllDone', cancelled: true });
          }
          break;
        case 'terminalOutput': {
          const term = terminals.get(msg.id);
          if (term) { appendTerminalLine(term, msg.text); }
          break;
        }
        case 'terminalExit': {
          const term = terminals.get(msg.id);
          if (term) {
            appendTerminalLine(term, '[进程已退出，退出码 ' + msg.code + ']');
            removeTerminal(msg.id);
          }
          break;
        }
        case 'terminalNote': terminalNote(msg.text); break;
        case 'terminalStdout': terminalStdout(msg.text); break;
        case 'settings': applyHostSettings(msg); break;
        case 'requestOutline': requestOutline(); break;
        case 'stopOutline': stopOutline(); break;
        case 'reveal': revealPosition(msg.line, msg.column); break;
        case 'diagnostics': applyDiagnostics(msg.path, msg.items); break;
        case 'clearDiagnostics': clearDiagnostics(msg.path); break;
        case 'debugLine': showDebugLine(msg.path, msg.line); break;
        case 'debugLineClear': clearDebugLine(); break;
        case 'breakpoints': applyBreakpointList(msg.path, msg.lines); break;
      }
    });
  }

  /* ---------- 宿主下发的设置 ---------- */

  function applyHostSettings(msg) {
    if (typeof msg.theme === 'string') { settings.theme = msg.theme; }
    if (typeof msg.saveEncoding === 'string') { applyEncoding(msg.saveEncoding); }
    if (typeof msg.fontFamily === 'string' && msg.fontFamily.length > 0) { settings.fontFamily = msg.fontFamily; }
    if (typeof msg.fontSize === 'number') { settings.fontSize = msg.fontSize; }
    if (typeof msg.fontLigatures === 'boolean') { settings.fontLigatures = msg.fontLigatures; }
    if (typeof msg.wordWrap === 'string') { settings.wordWrap = msg.wordWrap; }
    if (typeof msg.minimap === 'boolean') { settings.minimap = msg.minimap; }
    if (typeof msg.whitespace === 'string') { settings.whitespace = msg.whitespace; }
    if (typeof msg.cursorStyle === 'string') { settings.cursorStyle = msg.cursorStyle; }
    if (typeof msg.cursorBlinking === 'string') { settings.cursorBlinking = msg.cursorBlinking; }
    if (typeof msg.tabSize === 'number') { settings.tabSize = msg.tabSize; }
    if (typeof msg.insertSpaces === 'boolean') { settings.insertSpaces = msg.insertSpaces; }
    if (typeof msg.suggest === 'boolean') { settings.suggest = msg.suggest; }
    if (typeof msg.parameterHints === 'boolean') { settings.parameterHints = msg.parameterHints; }
    if (typeof msg.hover === 'boolean') { settings.hover = msg.hover; }
    if (typeof msg.bracketColorization === 'boolean') { settings.bracketColorization = msg.bracketColorization; }
    if (typeof msg.stickyScroll === 'boolean') { settings.stickyScroll = msg.stickyScroll; }
    if (typeof msg.occurrencesHighlight === 'boolean') { settings.occurrencesHighlight = msg.occurrencesHighlight; }
    if (typeof msg.uiZoom === 'number') { settings.uiZoom = msg.uiZoom; }
    if (typeof msg.autoSave === 'boolean') { settings.autoSave = msg.autoSave; }
    if (typeof msg.autoSaveSeconds === 'number') { settings.autoSaveSeconds = msg.autoSaveSeconds; }

    // 新建文件模板
    newFileTemplate = msg.insertTemplate === false ? '' : (msg.template || '');

    writeSettings();
    applyEditorOptions();
    setupAutoSave();
  }

  /* ---------- 自动保存 ---------- */

  let autoSaveTimer = null;

  function setupAutoSave() {
    if (autoSaveTimer) {
      clearInterval(autoSaveTimer);
      autoSaveTimer = null;
    }
    if (!settings.autoSave) { return; }

    const seconds = Math.min(600, Math.max(5, settings.autoSaveSeconds || 60));
    autoSaveTimer = setInterval(function () {
      for (const tab of tabs) {
        // 未保存过的文件没有路径，等用户保存后再自动保存
        if (tab.dirty && tab.path && tab.model && !tab.model.isDisposed()) {
          post({
            type: 'autoSave',
            path: tab.path,
            name: tab.name,
            content: tab.model.getValue()
          });
        }
      }
    }, seconds * 1000);
  }

  /* 编辑器内的快捷键：Ctrl+N 新建、Ctrl+S 保存、Ctrl+Shift+S 另存为、Ctrl+W 关闭
     F12 编译、F10 运行、F11 编译并运行 */
  document.addEventListener('keydown', function (e) {
    // 调试快捷键：F5 开始/继续、Shift+F5 停止、F7 单步跳过、F8 单步进入
    if (!e.ctrlKey && !e.altKey) {
      const debugKey = e.key.toLowerCase();
      if (debugKey === 'f5') {
        e.preventDefault();
        e.stopPropagation();
        post({ type: e.shiftKey ? 'debugStop' : 'debugContinue' });
        return;
      }
      if (!e.shiftKey && (debugKey === 'f7' || debugKey === 'f8')) {
        e.preventDefault();
        e.stopPropagation();
        post({ type: debugKey === 'f7' ? 'debugStepOver' : 'debugStepInto' });
        return;
      }
    }

    if (!e.ctrlKey && !e.altKey && !e.shiftKey) {
      const buildKey = e.key.toLowerCase();
      if (buildKey === 'f12' || buildKey === 'f10' || buildKey === 'f11') {
        e.preventDefault();
        e.stopPropagation();
        requestBuild(buildKey === 'f12' ? 'compile' : (buildKey === 'f10' ? 'run' : 'buildRun'));
        return;
      }
    }

    if (!e.ctrlKey || e.altKey) { return; }
    const key = e.key.toLowerCase();

    if (key === '`') {
      e.preventDefault();
      e.stopPropagation();
      toggleTerminal();
      return;
    }
    if (key === '=' || key === '+' || key === 'add') {
      e.preventDefault();
      e.stopPropagation();
      changeUiZoom(1);
      return;
    }
    if (key === '-' || key === 'subtract') {
      e.preventDefault();
      e.stopPropagation();
      changeUiZoom(-1);
      return;
    }
    if (key === '0') {
      e.preventDefault();
      e.stopPropagation();
      changeUiZoom(0);
      return;
    }
    if (key === 's') {
      e.preventDefault();
      e.stopPropagation();
      requestSave(e.shiftKey);
      return;
    }
    if (e.shiftKey) { return; }
    if (key === 'n') { e.preventDefault(); e.stopPropagation(); newUntitled(); }
    else if (key === 'w') { e.preventDefault(); e.stopPropagation(); handleCommand('closeActive'); }
  }, true);

  /* Ctrl + 滚轮缩放字体（只改 Monaco 的 fontSize，会被记住）
     用捕获阶段监听，避免被 Monaco 自己的滚动处理拦掉 */
  document.addEventListener('wheel', function (e) {
    if (!e.ctrlKey) { return; }
    e.preventDefault();
    e.stopPropagation();
    changeFontSize(e.deltaY < 0 ? 1 : -1);
  }, { passive: false, capture: true });

  post({ type: 'ready' });
  applyTheme();
  applyUiZoom(settings.uiZoom);
});
