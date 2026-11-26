function el(tag, props) {
  return Object.assign(document.createElement(tag), props);
}
export function button(text, className, onclick) {
  return el('button', { type: 'button', textContent: text, className, onclick });
}

// openMenu shows a list of actions next to a button. It lives outside the table,
// so the regular refresh does not close it and the table does not clip it.
let closeMenu = () => {};
export function openMenu(anchor, items) {
  closeMenu();
  const menu = el('div', { className: 'menu' });
  menu.setAttribute('role', 'menu');
  for (const item of items) {
    if (!item) {
      menu.append(el('hr'));
      continue;
    }
    const entry = button(item.text, item.danger ? 'danger' : '', () => {
      // What cannot be undone asks a second time, naming what it will do.
      if (item.confirm && !entry.dataset.armed) {
        entry.dataset.armed = '1';
        entry.textContent = item.confirm;
        entry.className = 'danger';
        return;
      }
      closeMenu();
      item.run();
    });
    entry.setAttribute('role', 'menuitem');
    entry.disabled = !!item.why;
    if (item.why) entry.append(el('small', { textContent: item.why }));
    menu.append(entry);
  }
  document.body.append(menu);
  const box = anchor.getBoundingClientRect();
  menu.style.left = Math.max(8, Math.min(box.right, innerWidth - 8) - menu.offsetWidth) + 'px';
  const below = box.bottom + 4 + menu.offsetHeight <= innerHeight;
  menu.style.top = (below ? box.bottom + 4 : Math.max(8, box.top - 4 - menu.offsetHeight)) + 'px';

  const entries = () => [...menu.querySelectorAll('button:not(:disabled)')];
  const outside = e => { if (!menu.contains(e.target)) closeMenu(); };
  const keys = e => {
    const list = entries();
    const at = list.indexOf(document.activeElement);
    if (e.key === 'Escape') { closeMenu(); anchor.focus(); }
    if (e.key === 'ArrowDown') { e.preventDefault(); list[(at + 1) % list.length]?.focus(); }
    if (e.key === 'ArrowUp') { e.preventDefault(); list[(at - 1 + list.length) % list.length]?.focus(); }
  };
  closeMenu = () => {
    menu.remove();
    document.removeEventListener('mousedown', outside);
    document.removeEventListener('keydown', keys);
    window.removeEventListener('scroll', closeMenu, true);
    closeMenu = () => {};
  };
  document.addEventListener('mousedown', outside);
  document.addEventListener('keydown', keys);
  window.addEventListener('scroll', closeMenu, true);
  entries()[0]?.focus();
}
