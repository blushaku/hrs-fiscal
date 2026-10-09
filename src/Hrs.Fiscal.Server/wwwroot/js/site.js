// HRS Fiscal admin: pop-up editors and "are you sure?" before saving. Works without a framework; pages still work
// without JavaScript (Edit links fall back to ?Edit=… and the server opens the editor).
(function () {
    'use strict';

    function open(dialog) {
        if (!dialog) return;
        if (dialog.open) { if (dialog.matches(':modal')) return; dialog.close(); } // server-opened (no-JS fallback) -> make it modal
        dialog.showModal();
    }

    // Buttons with data-dialog-open="id" open that dialog; data-dialog-close closes the enclosing one.
    document.addEventListener('click', function (e) {
        var opener = e.target.closest('[data-dialog-open]');
        if (opener) { e.preventDefault(); open(document.getElementById(opener.getAttribute('data-dialog-open'))); return; }
        var closer = e.target.closest('[data-dialog-close]');
        if (closer) {
            e.preventDefault();
            var d = closer.closest('dialog');
            if (d) { resetConfirm(d.querySelector('form[data-confirm]')); d.close(); }
            // Editors opened by the server (?Edit=…, ?Form=…) return to the clean page; others just close.
            if (closer.hasAttribute('data-return') && window.location.search) window.location.href = closer.getAttribute('data-return');
        }
    });

    // A click on the backdrop closes the dialog (not while confirming).
    document.addEventListener('mousedown', function (e) {
        if (e.target.tagName === 'DIALOG' && !e.target.querySelector('.confirm:not([hidden])')) e.target.close();
    });

    // Editors the server wants open (validation errors, ?Edit=… links).
    window.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('dialog[data-autoopen="1"]').forEach(open);
    });

    // ---- confirmation before saving ----------------------------------------------------------------
    function labelOf(el) {
        if (el.getAttribute('aria-label')) return el.getAttribute('aria-label');
        var label = el.closest('label');
        if (!label) return el.name;
        var text = '';
        label.childNodes.forEach(function (n) { if (n.nodeType === 3) text += n.textContent; });
        return text.trim() || el.name;
    }

    function shown(el) {
        if (el.type === 'checkbox') return el.checked ? 'yes' : 'no';
        if (el.tagName === 'SELECT') return el.options[el.selectedIndex] ? el.options[el.selectedIndex].text : '';
        if (el.type === 'password') return el.value ? '(new password)' : '';
        if (el.type === 'file') return el.files.length ? el.files[0].name : '';
        return el.value === '' ? '(empty)' : el.value;
    }

    function before(el) {
        if (el.type === 'checkbox') return el.defaultChecked ? 'yes' : 'no';
        if (el.tagName === 'SELECT') {
            for (var i = 0; i < el.options.length; i++) if (el.options[i].defaultSelected) return el.options[i].text;
            return el.options[0] ? el.options[0].text : '';
        }
        return el.defaultValue === '' ? '(empty)' : el.defaultValue;
    }

    function changed(el) {
        if (el.type === 'checkbox' || el.type === 'radio') return el.checked !== el.defaultChecked;
        if (el.tagName === 'SELECT') {
            for (var i = 0; i < el.options.length; i++) if (el.options[i].selected !== el.options[i].defaultSelected) return true;
            return false;
        }
        if (el.type === 'file') return el.files.length > 0;
        if (el.type === 'password') return el.value !== '';
        return el.value !== el.defaultValue;
    }

    function resetConfirm(form) {
        if (!form) return;
        var box = form.querySelector('.confirm');
        if (box) box.hidden = true;
        form.querySelectorAll('[data-hide-on-confirm]').forEach(function (n) { n.hidden = false; });
        delete form.dataset.confirmed;
    }

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form.matches('form[data-confirm]') || form.dataset.confirmed === '1') return;
        e.preventDefault();
        var fields = Array.prototype.filter.call(form.elements, function (el) {
            return el.name && !el.disabled && el.type !== 'hidden' && el.type !== 'submit' && el.type !== 'button' && changed(el);
        });
        var radioSeen = {};
        var lines = [];
        fields.forEach(function (el) {
            if (el.type === 'radio') { if (radioSeen[el.name] || !el.checked) return; radioSeen[el.name] = 1; lines.push(labelOf(el)); return; }
            var isNew = form.hasAttribute('data-new');
            if (el.type === 'checkbox' && el.getAttribute('aria-label')) { lines.push(labelOf(el) + ': ' + (el.checked ? 'allowed' : 'not allowed')); return; }
            lines.push(isNew ? labelOf(el) + ': ' + shown(el) : labelOf(el) + ': ' + before(el) + ' → ' + shown(el));
        });
        var box = form.querySelector('.confirm');
        if (!box) { form.dataset.confirmed = '1'; form.requestSubmit ? form.requestSubmit() : form.submit(); return; }
        var list = box.querySelector('ul');
        var title = box.querySelector('.confirm-title');
        list.innerHTML = '';
        lines.forEach(function (t) { var li = document.createElement('li'); li.textContent = t; list.appendChild(li); });
        var nothing = lines.length === 0 && !form.hasAttribute('data-new');
        title.textContent = nothing ? 'Nothing has changed.' : form.getAttribute('data-confirm');
        box.querySelector('[data-confirm-yes]').hidden = nothing;
        box.hidden = false;
        form.querySelectorAll('[data-hide-on-confirm]').forEach(function (n) { n.hidden = true; });
        (nothing ? box.querySelector('[data-confirm-back]') : box.querySelector('[data-confirm-yes]')).focus();
    });

    document.addEventListener('click', function (e) {
        var yes = e.target.closest('[data-confirm-yes]');
        if (yes) {
            var form = yes.closest('form');
            form.dataset.confirmed = '1';
            yes.disabled = true;
            yes.textContent = 'Saving…';
            HTMLFormElement.prototype.submit.call(form);
            return;
        }
        var back = e.target.closest('[data-confirm-back]');
        if (back) { e.preventDefault(); resetConfirm(back.closest('form')); }
    });
})();
