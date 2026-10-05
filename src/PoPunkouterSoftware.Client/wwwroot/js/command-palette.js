/**
 * Global Command Palette (Cmd+K / Ctrl+K)
 * Provides instantaneous keyboard-driven navigation and actions across the portfolio.
 */
(function () {
    'use strict';

    var overlay = null;
    var input = null;
    var list = null;
    var selectedIndex = 0;
    var isOpen = false;

    var COMMANDS = [
        { id: 'nav-apps', title: 'Go to Apps Portfolio', icon: 'home', category: 'Navigation', action: function () { navigate('/'); } },
        { id: 'nav-azure', title: 'Go to Azure Ops Dashboard', icon: 'monitor_heart', category: 'Navigation', action: function () { navigate('/azure'); } },
        { id: 'nav-users', title: 'Go to User Sign-Ins', icon: 'group', category: 'Navigation', action: function () { navigate('/users'); } },
        { id: 'nav-settings', title: 'Open Settings & Preferences', icon: 'settings', category: 'Navigation', action: function () { navigate('/settings'); } },
        { id: 'act-theme', title: 'Toggle Light / Dark Theme', icon: 'dark_mode', category: 'Actions', action: function () { if (window.themeKit) window.themeKit.toggle(); } },
        { id: 'act-sound', title: 'Toggle Audio SFX & Sonification', icon: 'volume_up', category: 'Actions', action: function () { if (window.audioKit) window.audioKit.toggle(); } },
        { id: 'act-sonify', title: 'Play Fleet Sonification Audio', icon: 'graphic_eq', category: 'Actions', action: function () { if (window.audioKit) window.audioKit.play('success'); } },
        { id: 'act-reload', title: 'Hard Reload Application', icon: 'refresh', category: 'System', action: function () { window.location.reload(); } }
    ];

    function navigate(url) {
        window.location.href = url;
    }

    function createPalette() {
        if (overlay) return;

        overlay = document.createElement('div');
        overlay.className = 'app-palette-backdrop';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', 'Command Palette');

        var modal = document.createElement('div');
        modal.className = 'app-palette-modal';

        var header = document.createElement('div');
        header.className = 'app-palette-header';

        var searchIcon = document.createElement('span');
        searchIcon.className = 'material-symbols-outlined app-palette-search-icon';
        searchIcon.textContent = 'search';
        searchIcon.setAttribute('aria-hidden', 'true');

        input = document.createElement('input');
        input.type = 'text';
        input.className = 'app-palette-input';
        input.placeholder = 'Type a command or search (e.g. "azure", "theme", "settings")...';
        input.setAttribute('aria-autocomplete', 'list');

        var kbd = document.createElement('kbd');
        kbd.className = 'app-palette-kbd';
        kbd.textContent = 'ESC';

        header.appendChild(searchIcon);
        header.appendChild(input);
        header.appendChild(kbd);

        list = document.createElement('div');
        list.className = 'app-palette-list';
        list.setAttribute('role', 'listbox');

        modal.appendChild(header);
        modal.appendChild(list);
        overlay.appendChild(modal);

        document.body.appendChild(overlay);

        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) close();
        });

        input.addEventListener('input', function () {
            selectedIndex = 0;
            renderList();
        });

        input.addEventListener('keydown', function (e) {
            var items = getFilteredCommands();
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                selectedIndex = (selectedIndex + 1) % Math.max(1, items.length);
                renderList();
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                selectedIndex = (selectedIndex - 1 + items.length) % Math.max(1, items.length);
                renderList();
            } else if (e.key === 'Enter') {
                e.preventDefault();
                if (items[selectedIndex]) {
                    close();
                    items[selectedIndex].action();
                }
            } else if (e.key === 'Escape') {
                close();
            }
        });
    }

    function getFilteredCommands() {
        var query = (input ? input.value : '').toLowerCase().trim();
        if (!query) return COMMANDS;
        return COMMANDS.filter(function (cmd) {
            return cmd.title.toLowerCase().indexOf(query) !== -1 ||
                   cmd.category.toLowerCase().indexOf(query) !== -1;
        });
    }

    function renderList() {
        if (!list) return;
        list.innerHTML = '';
        var items = getFilteredCommands();

        if (items.length === 0) {
            var empty = document.createElement('div');
            empty.className = 'app-palette-empty';
            empty.textContent = 'No commands match your query.';
            list.appendChild(empty);
            return;
        }

        items.forEach(function (cmd, idx) {
            var el = document.createElement('div');
            el.className = 'app-palette-item' + (idx === selectedIndex ? ' is-active' : '');
            el.setAttribute('role', 'option');
            el.setAttribute('aria-selected', idx === selectedIndex ? 'true' : 'false');

            var icon = document.createElement('span');
            icon.className = 'material-symbols-outlined app-palette-item-icon';
            icon.textContent = cmd.icon;
            icon.setAttribute('aria-hidden', 'true');

            var label = document.createElement('span');
            label.className = 'app-palette-item-title';
            label.textContent = cmd.title;

            var category = document.createElement('span');
            category.className = 'app-palette-item-category';
            category.textContent = cmd.category;

            el.appendChild(icon);
            el.appendChild(label);
            el.appendChild(category);

            el.addEventListener('click', function () {
                close();
                cmd.action();
            });

            el.addEventListener('mouseenter', function () {
                selectedIndex = idx;
                renderList();
            });

            list.appendChild(el);
        });

        var activeEl = list.children[selectedIndex];
        if (activeEl && activeEl.scrollIntoViewIfNeeded) {
            activeEl.scrollIntoViewIfNeeded(false);
        }
    }

    function open() {
        createPalette();
        isOpen = true;
        overlay.classList.add('is-open');
        input.value = '';
        selectedIndex = 0;
        renderList();
        setTimeout(function () { input.focus(); }, 50);
        if (window.audioKit) window.audioKit.play('open');
    }

    function close() {
        if (!isOpen) return;
        isOpen = false;
        if (overlay) overlay.classList.remove('is-open');
        if (window.audioKit) window.audioKit.play('close');
    }

    document.addEventListener('click', function (e) {
        if (!e.target || !e.target.closest) return;
        var btn = e.target.closest('[data-palette-toggle]');
        if (btn) {
            e.preventDefault();
            if (isOpen) close(); else open();
        }
    });

    document.addEventListener('keydown', function (e) {
        if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            if (isOpen) close(); else open();
        } else if (e.key === 'Escape' && isOpen) {
            close();
        }
    });

    window.commandPalette = {
        open: open,
        close: close,
        toggle: function () { if (isOpen) close(); else open(); }
    };
})();
