export default function (view) {
    'use strict';

    var pollTimer = null;

    function apiFetch(method, path, body) {
        var baseUrl = ApiClient.serverAddress();
        var token = ApiClient.accessToken();
        var opts = {
            method: method,
            headers: { 'Authorization': 'MediaBrowser Token="' + token + '"' }
        };
        if (body) {
            opts.headers['Content-Type'] = 'application/json';
            opts.body = JSON.stringify(body);
        }
        return fetch(baseUrl + '/' + path, opts).then(function (r) {
            if (!r.ok) throw new Error(r.status + ' ' + r.statusText);
            return r.json();
        });
    }

    function loadCollections() {
        var select = view.querySelector('#ytCollection');
        apiFetch('GET', 'Items?IncludeItemTypes=BoxSet&Recursive=true&SortBy=SortName&SortOrder=Ascending')
            .then(function (result) {
                while (select.options.length > 1) {
                    select.remove(1);
                }
                (result.Items || []).forEach(function (item) {
                    var opt = document.createElement('option');
                    opt.value = item.Name;
                    opt.textContent = item.Name;
                    select.appendChild(opt);
                });
            });
    }

    view.addEventListener('viewshow', function () {
        loadCollections();
    });

    view.querySelector('#YouTubeDLDownloadForm').addEventListener('submit', function (e) {
        e.preventDefault();
        stopPolling();

        var url = view.querySelector('#ytUrl').value.trim();
        if (!url) return;

        var body = { Url: url };
        var collection = view.querySelector('#ytCollection').value;
        var genre = view.querySelector('#ytGenre').value.trim();
        if (collection) body.Collection = collection;
        if (genre) body.Genre = genre;

        setStatus('Submitting...', '', '');
        view.querySelector('#ytSubmitBtn').disabled = true;

        apiFetch('POST', 'api/youtube/download', body).then(function (resp) {
            setStatus('Queued', '', '');
            startPolling(resp.TaskId);
        }).catch(function (err) {
            setStatus('', '', 'Failed to start download: ' + err.message);
            view.querySelector('#ytSubmitBtn').disabled = false;
        });
    });

    function startPolling(taskId) {
        pollTimer = setInterval(function () {
            apiFetch('GET', 'api/youtube/status/' + taskId).then(function (resp) {
                var label = resp.Status.replace(/_/g, ' ');
                label = label.charAt(0).toUpperCase() + label.slice(1);

                if (resp.Status === 'complete') {
                    setStatus(label, resp.Path || '', '');
                    stopPolling();
                    view.querySelector('#ytSubmitBtn').disabled = false;
                } else if (resp.Status === 'error') {
                    setStatus(label, '', resp.Error || 'Unknown error');
                    stopPolling();
                    view.querySelector('#ytSubmitBtn').disabled = false;
                } else {
                    setStatus(label, '', '');
                }
            });
        }, 2000);
    }

    function stopPolling() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    view.addEventListener('viewhide', stopPolling);

    function setStatus(text, path, error) {
        var container = view.querySelector('#ytStatus');
        container.style.display = 'block';
        view.querySelector('#ytStatusText').textContent = text;
        view.querySelector('#ytStatusPath').textContent = path;
        view.querySelector('#ytStatusError').textContent = error;
    }
}
