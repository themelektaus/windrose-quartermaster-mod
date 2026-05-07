(function () {
    'use strict';

    var statusEl = document.getElementById('status');
    var listEl = document.getElementById('items');

    fetch('/api/items')
        .then(function (response) {
            if (!response.ok) {
                throw new Error('HTTP ' + response.status);
            }
            return response.json();
        })
        .then(function (items) {
            statusEl.textContent = items.length + ' items';
            var fragment = document.createDocumentFragment();
            for (var i = 0; i < items.length; i++) {
                var item = items[i];
                var li = document.createElement('li');
                if (item.icon) {
                    var img = document.createElement('img');
                    img.src = item.icon;
                    img.alt = item.name;
                    img.loading = 'lazy';
                    li.appendChild(img);
                } else {
                    var placeholder = document.createElement('div');
                    placeholder.className = 'placeholder';
                    li.appendChild(placeholder);
                }
                var name = document.createElement('span');
                name.className = 'name';
                name.textContent = item.name;
                li.appendChild(name);
                fragment.appendChild(li);
            }
            listEl.appendChild(fragment);
        })
        .catch(function (err) {
            statusEl.textContent = 'Failed to load items: ' + err.message;
        });
})();
