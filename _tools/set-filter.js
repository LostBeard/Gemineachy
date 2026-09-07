// Choose which tensors the STT self-test dumps, and how many lines, without rebuilding the extension.
document.documentElement.setAttribute('data-gem-sttfilter', window.__sttFilter || 'last_hidden_state');
document.documentElement.setAttribute('data-gem-sttmax', String(window.__sttMax || 12));
document.documentElement.removeAttribute('data-gem-sttdump');
'filter=' + document.documentElement.getAttribute('data-gem-sttfilter')
  + ' max=' + document.documentElement.getAttribute('data-gem-sttmax')
