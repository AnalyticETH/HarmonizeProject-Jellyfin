import assert from "node:assert/strict";
import fs from "node:fs";
import vm from "node:vm";

const file = "Jellyfin.Plugin.Hue/Configuration/configPage.html";
const html = fs.readFileSync(file, "utf8");
const scriptMatch = html.match(/<script type="text\/javascript">([\s\S]*?)<\/script>/);

if (!scriptMatch) {
    throw new Error(`${file} does not contain the configuration script`);
}

const exportCases = [
    {
        method: "exportConfiguration",
        key: "configurationExport",
        flag: "_hueConfigurationExporting",
        button: "#exportConfigurationBtn",
        route: "HueSync/Configuration/Export",
        fileName: "jellyfin-hue-configuration.json",
        dataType: "json",
        contentType: "application/json",
        response: { UserMappings: [], Marker: "configuration" }
    },
    {
        method: "exportSceneScheduleConflicts",
        key: "sceneScheduleConflictsExport",
        flag: "_hueSceneScheduleConflictsExporting",
        button: "#exportSceneScheduleConflictsBtn",
        route: "HueSync/SceneSchedules/Conflicts?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-schedule-conflicts.json",
        dataType: "json",
        contentType: "application/json",
        response: { Conflicts: [], Marker: "conflicts" }
    },
    {
        method: "exportSceneScheduleConflictsCsv",
        key: "sceneScheduleConflictsCsvExport",
        flag: "_hueSceneScheduleConflictsCsvExporting",
        button: "#exportSceneScheduleConflictsCsvBtn",
        route: "HueSync/SceneSchedules/Conflicts/ExportCsv?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-schedule-conflicts.csv",
        dataType: "text",
        contentType: "text/csv;charset=utf-8",
        response: "kind,marker\nconflicts,csv"
    },
    {
        method: "exportSceneScheduleOccurrences",
        key: "sceneScheduleOccurrencesExport",
        flag: "_hueSceneScheduleOccurrencesExporting",
        button: "#exportSceneScheduleOccurrencesBtn",
        route: "HueSync/SceneSchedules/Occurrences?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-schedule-occurrences.json",
        dataType: "json",
        contentType: "application/json",
        response: { Occurrences: [], Marker: "occurrences" }
    },
    {
        method: "exportSceneScheduleOccurrencesCsv",
        key: "sceneScheduleOccurrencesCsvExport",
        flag: "_hueSceneScheduleOccurrencesCsvExporting",
        button: "#exportSceneScheduleOccurrencesCsvBtn",
        route: "HueSync/SceneSchedules/Occurrences/ExportCsv?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-schedule-occurrences.csv",
        dataType: "text",
        contentType: "text/csv;charset=utf-8",
        response: "kind,marker\noccurrences,csv"
    },
    {
        method: "exportSceneScheduleCalendar",
        key: "sceneScheduleCalendarExport",
        flag: "_hueSceneScheduleCalendarExporting",
        button: "#exportSceneScheduleCalendarBtn",
        route: "HueSync/SceneSchedules/Calendar?limit=50&days=31&scheduleId=cue-1",
        fileName: "jellyfin-hue-scene-cues.ics",
        dataType: "text",
        contentType: "text/calendar;charset=utf-8",
        response: "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nEND:VCALENDAR\r\n"
    },
    {
        method: "exportSceneScheduleHistory",
        key: "sceneScheduleHistoryExport",
        flag: "_hueSceneScheduleHistoryExporting",
        button: "#exportSceneScheduleHistoryBtn",
        route: "HueSync/SceneSchedules/History/Export?limit=100&scheduleId=cue-1&outcome=Error",
        fileName: "jellyfin-hue-scene-schedule-history.json",
        dataType: "json",
        contentType: "application/json",
        response: { History: [], Marker: "schedule-history" }
    },
    {
        method: "exportSceneScheduleHistoryCsv",
        key: "sceneScheduleHistoryCsvExport",
        flag: "_hueSceneScheduleHistoryCsvExporting",
        button: "#exportSceneScheduleHistoryCsvBtn",
        route: "HueSync/SceneSchedules/History/ExportCsv?limit=100&scheduleId=cue-1&outcome=Error",
        fileName: "jellyfin-hue-scene-schedule-history.csv",
        dataType: "text",
        contentType: "text/csv;charset=utf-8",
        response: "kind,marker\nschedule-history,csv"
    },
    {
        method: "exportSessionHistory",
        key: "sessionHistoryExport",
        flag: "_hueSessionHistoryExporting",
        button: "#exportSessionHistoryBtn",
        route: "HueSync/History/Export?limit=25&outcome=Stopped",
        fileName: "jellyfin-hue-session-history.json",
        dataType: "json",
        contentType: "application/json",
        response: { Sessions: [], Marker: "session-history" }
    },
    {
        method: "exportSessionHistoryCsv",
        key: "sessionHistoryCsvExport",
        flag: "_hueSessionHistoryCsvExporting",
        button: "#exportSessionHistoryCsvBtn",
        route: "HueSync/History/ExportCsv?limit=25&outcome=Stopped",
        fileName: "jellyfin-hue-session-history.csv",
        dataType: "text",
        contentType: "text/csv;charset=utf-8",
        response: "kind,marker\nsession-history,csv"
    }
];

function makeElement(tagName = "div") {
    const element = {
        tagName: String(tagName).toUpperCase(),
        disabled: false,
        value: "",
        textContent: "",
        selectedIndex: 0,
        options: [],
        dataset: {},
        attributes: {},
        style: {},
        children: [],
        listeners: {},
        classList: {
            contains: () => false,
            add: () => {},
            remove: () => {}
        },
        addEventListener(eventName, handler) {
            this.listeners[eventName] = handler;
        },
        setAttribute(name, value) {
            this.attributes[name] = String(value);
        },
        getAttribute(name) {
            return Object.prototype.hasOwnProperty.call(this.attributes, name) ? this.attributes[name] : null;
        },
        appendChild(child) {
            this.children.push(child);
            if (this.tagName === "SELECT" && child && child.tagName === "OPTION") {
                this.options.push(child);
                if (this.options.length === 1) {
                    this.value = child.value;
                    this.selectedIndex = 0;
                }
            }
            return child;
        },
        removeChild(child) {
            const index = this.children.indexOf(child);
            if (index >= 0) this.children.splice(index, 1);
            return child;
        },
        click() {},
        querySelector() {
            return makeElement();
        },
        closest() {
            return null;
        }
    };
    Object.defineProperty(element, "innerHTML", {
        configurable: true,
        get() {
            return this._innerHTML || "";
        },
        set(value) {
            this._innerHTML = String(value || "");
            this.children = [];
            if (this.tagName === "SELECT") {
                this.options = [];
                this.selectedIndex = -1;
                this.value = "";
            }
        }
    });
    return element;
}

function makeHarness() {
    const downloads = [];
    const blobs = new Map();
    const requests = [];
    const readers = [];
    let blobNumber = 0;
    const pageElements = new Map();
    let activePage = null;

    const body = makeElement("body");
    const document = {
        body,
        querySelector(selector) {
            if (selector === '.pluginConfigurationPage' && activePage) return activePage;
            return makeElement();
        },
        getElementById(id) {
            if (!pageElements.has(id)) {
                pageElements.set(id, makeElement());
            }
            return pageElements.get(id);
        },
        querySelectorAll() {
            return [];
        },
        createTextNode(value) {
            return { textContent: String(value || "") };
        },
        createElement(tagName) {
            const element = makeElement(tagName);
            if (String(tagName).toLowerCase() === "a") {
                element.click = () => {
                    downloads.push({
                        fileName: element.download,
                        href: element.href,
                        blob: blobs.get(element.href)
                    });
                };
            }
            return element;
        },
        addEventListener() {}
    };

    const url = {
        createObjectURL(blob) {
            const value = `blob:hue-config-contract-${++blobNumber}`;
            blobs.set(value, blob);
            return value;
        },
        revokeObjectURL(value) {
            blobs.delete(value);
        }
    };

    class TestBlob {
        constructor(parts, options) {
            this.parts = parts;
            this.type = options && options.type;
        }
    }

    class TestFileReader {
        constructor() {
            this.result = "";
            this.error = null;
            this.onload = null;
            this.onerror = null;
            this.aborted = false;
            readers.push(this);
        }

        readAsText(file) {
            this.file = file;
        }

        abort() {
            this.aborted = true;
        }

        resolve(result) {
            this.result = result;
            if (typeof this.onload === "function") this.onload();
        }

        reject(error) {
            this.error = error;
            if (typeof this.onerror === "function") this.onerror();
        }
    }

    const dashboard = {
        alerts: [],
        hideLoadingMsg() {},
        showLoadingMsg() {},
        confirm() {},
        alert(message) {
            this.alerts.push(message);
        }
    };

    function makeDeferred(options) {
        let resolvePromise;
        let rejectPromise;
        const promise = new Promise((resolve, reject) => {
            resolvePromise = resolve;
            rejectPromise = reject;
        });
        promise.options = options;
        promise.aborted = false;
        promise.abort = () => {
            promise.aborted = true;
        };
        return {
            promise,
            options,
            resolve: resolvePromise,
            reject: rejectPromise
        };
    }

    const apiClient = {
        getUrl(value) {
            return value;
        },
        getJSON(url) {
            const deferred = makeDeferred({
                type: "GET",
                url,
                dataType: "json"
            });
            requests.push(deferred);
            return deferred.promise;
        },
        ajax(options) {
            const deferred = makeDeferred(options);
            requests.push(deferred);
            return deferred.promise;
        }
    };

    const context = vm.createContext({
        ApiClient: apiClient,
        Blob: TestBlob,
        FileReader: TestFileReader,
        Dashboard: dashboard,
        URL: url,
        console: {
            error() {},
            warn() {},
            log() {}
        },
        document,
        window: {
            document,
            URL: url,
            setTimeout(callback) {
                callback();
                return 1;
            },
            clearTimeout() {}
        },
        setTimeout(callback) {
            callback();
            return 1;
        },
        clearTimeout() {},
        AbortController
    });

    new vm.Script(`"use strict";\n${scriptMatch[1]}`, { filename: file }).runInContext(context);

    const page = {
        _huePageActive: true,
        _huePageGeneration: 7,
        querySelector(selector) {
            if (typeof selector !== "string" || selector[0] !== "#") return null;
            const id = selector.slice(1);
            if (!pageElements.has(id)) {
                pageElements.set(id, makeElement());
            }
            return pageElements.get(id);
        },
        querySelectorAll() {
            return [];
        }
    };
    activePage = page;

    for (const [id, value] of [
        ["sceneScheduleConflictFilter", "cue-1"],
        ["sceneScheduleOccurrenceFilter", "cue-1"],
        ["sceneScheduleReportHorizon", "31"],
        ["sceneScheduleHistoryOutcome", "Error"],
        ["sceneScheduleHistoryCueFilter", "cue-1"],
        ["sessionHistoryOutcome", "Stopped"]
    ]) {
        const element = page.querySelector(`#${id}`);
        element.value = value;
        element.selectedIndex = 1;
        element.options = [
            { value: "", textContent: "All cues" },
            { value, textContent: "Cue One" }
        ];
    }

    return {
        page,
        api: context.HueConfigurationPage,
        requests,
        readers,
        downloads,
        blobs,
        dashboard
    };
}

function mutateQuery(page, method) {
    if (method.includes("Conflicts")) {
        page.querySelector("#sceneScheduleConflictFilter").value = "cue-2";
    } else if (method.includes("Occurrences") || method.includes("Calendar")) {
        page.querySelector("#sceneScheduleOccurrenceFilter").value = "cue-2";
    } else if (method.includes("SceneScheduleHistory")) {
        page.querySelector("#sceneScheduleHistoryOutcome").value = "Succeeded";
    } else if (method.includes("Configuration")) {
        page._huePageGeneration += 1;
    } else {
        page.querySelector("#sessionHistoryOutcome").value = "Completed";
    }
}

function assertExportPayload(testCase, download) {
    assert.equal(download.fileName, testCase.fileName, `${testCase.method} file name`);
    assert.ok(download.blob, `${testCase.method} creates a blob`);
    assert.equal(download.blob.type, testCase.contentType, `${testCase.method} content type`);
    const payload = download.blob.parts.join("");
    if (testCase.dataType === "json") {
        assert.deepEqual(JSON.parse(payload), testCase.response, `${testCase.method} JSON payload`);
    } else {
        assert.equal(payload, testCase.response, `${testCase.method} text payload`);
    }
}

async function testSuccessfulExport(testCase) {
    const harness = makeHarness();
    const { page, api, requests, downloads } = harness;
    const operation = api[testCase.method](page);
    assert.ok(operation && typeof operation.then === "function", `${testCase.method} returns a promise`);
    assert.equal(requests.length, 1, `${testCase.method} starts one request`);
    assert.equal(requests[0].options.type, "GET", `${testCase.method} uses GET`);
    assert.equal(requests[0].options.dataType, testCase.dataType, `${testCase.method} response type`);
    assert.equal(requests[0].options.url, testCase.route, `${testCase.method} query scope`);
    assert.equal(page[testCase.flag], true, `${testCase.method} marks itself busy`);
    assert.equal(page.querySelector(testCase.button).disabled, true, `${testCase.method} disables its button`);

    requests[0].resolve(testCase.response);
    await operation;

    assert.equal(downloads.length, 1, `${testCase.method} downloads once`);
    assertExportPayload(testCase, downloads[0]);
    assert.equal(page[testCase.flag], false, `${testCase.method} clears busy state`);
    assert.equal(page.querySelector(testCase.button).disabled, false, `${testCase.method} re-enables its button`);
}

async function testStaleQuerySuppressesExport(testCase) {
    const harness = makeHarness();
    const { page, api, requests, downloads } = harness;
    const operation = api[testCase.method](page);
    assert.equal(requests.length, 1, `${testCase.method} starts a stale-query request`);
    mutateQuery(page, testCase.method);
    requests[0].resolve(testCase.response);
    await operation;

    assert.equal(downloads.length, 0, `${testCase.method} suppresses a stale-query download`);
    assert.equal(page[testCase.flag], false, `${testCase.method} clears stale-query busy state`);
    assert.equal(page.querySelector(testCase.button).disabled, false, `${testCase.method} re-enables after stale query`);
    assert.equal(page._huePageRequests[testCase.key], undefined, `${testCase.method} removes stale request record`);
    assert.equal(requests[0].promise.aborted, true, `${testCase.method} aborts stale request record`);
}

async function testInvalidatedPageSuppressesExport(testCase) {
    const harness = makeHarness();
    const { page, api, requests, downloads } = harness;
    const operation = api[testCase.method](page);
    assert.equal(requests.length, 1, `${testCase.method} starts an invalidation request`);
    api.invalidatePageLifecycle(page);
    assert.equal(page._huePageActive, false, `${testCase.method} marks page inactive`);
    assert.equal(page[testCase.flag], false, `${testCase.method} clears busy state on invalidation`);
    assert.equal(page.querySelector(testCase.button).disabled, false, `${testCase.method} re-enables button on invalidation`);
    assert.equal(page._huePageRequests[testCase.key], undefined, `${testCase.method} removes invalidated request record`);
    assert.equal(requests[0].promise.aborted, true, `${testCase.method} aborts invalidated request`);
    requests[0].resolve(testCase.response);
    await operation;
    assert.equal(downloads.length, 0, `${testCase.method} suppresses an invalidated download`);
}

async function testCurrentFailure(testCase) {
    const harness = makeHarness();
    const { page, api, requests, downloads } = harness;
    const operation = api[testCase.method](page);
    requests[0].reject(new Error("deterministic export failure"));
    await operation;
    assert.equal(downloads.length, 0, `${testCase.method} does not download failures`);
    assert.equal(page[testCase.flag], false, `${testCase.method} clears failed busy state`);
    assert.equal(page.querySelector(testCase.button).disabled, false, `${testCase.method} re-enables after failure`);
}

async function testDuplicateClickIsBounded(testCase) {
    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    const first = api[testCase.method](page);
    const second = api[testCase.method](page);
    assert.ok(first && typeof first.then === "function", `${testCase.method} first click returns a promise`);
    if (second !== undefined) {
        assert.ok(second && typeof second.then === "function", `${testCase.method} duplicate click returns only a settled no-op promise`);
    }
    assert.equal(requests.length, 1, `${testCase.method} keeps one in-flight request`);
    requests[0].resolve(testCase.response);
    await first;
}

async function testHistoryClearLifecycleGuards() {
    const cases = [
        {
            method: "clearSceneScheduleHistory",
            key: "sceneScheduleHistoryClear",
            flag: "_hueSceneScheduleHistoryClearing",
            button: "#clearSceneScheduleHistoryBtn",
            status: "#sceneScheduleHistoryStatus",
            route: "HueSync/SceneSchedules/History",
            successText: "2 cue runs cleared.",
            response: { ClearedCount: 2 },
            reload(api, state) {
                api.loadSceneScheduleRuntimeStatus = () => {
                    state.runtimeLoads += 1;
                    return Promise.resolve();
                };
                api.loadSceneScheduleHistory = () => {
                    state.historyLoads += 1;
                    return Promise.resolve();
                };
            }
        },
        {
            method: "clearSessionHistory",
            key: "sessionHistoryClear",
            flag: "_hueSessionHistoryClearing",
            button: "#clearSessionHistoryBtn",
            status: "#sessionHistoryStatus",
            route: "HueSync/History",
            successText: "2 sessions cleared.",
            response: { ClearedCount: 2 },
            reload(api, state) {
                api.loadSessionHistory = () => {
                    state.historyLoads += 1;
                    return Promise.resolve();
                };
            }
        }
    ];

    for (const testCase of cases) {
        const confirmationHarness = makeHarness();
        const confirmationPage = confirmationHarness.page;
        const confirmationApi = confirmationHarness.api;
        const confirmationButton = confirmationPage.querySelector(testCase.button);
        let confirmation;
        let confirmationCalls = 0;
        confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
            confirmationCalls += 1;
            confirmation = callback;
        };
        const confirmationOperation = confirmationApi[testCase.method](confirmationPage);
        assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", `${testCase.method} returns a tracked confirmation promise`);
        assert.equal(confirmationHarness.requests.length, 0, `${testCase.method} waits for confirmation before mutating history`);
        assert.equal(confirmationCalls, 1, `${testCase.method} opens one confirmation`);
        assert.equal(confirmationButton.disabled, true, `${testCase.method} disables its button while confirmation is open`);
        const duplicateConfirmation = confirmationApi[testCase.method](confirmationPage);
        await duplicateConfirmation;
        assert.equal(confirmationCalls, 1, `${testCase.method} suppresses duplicate confirmation while pending`);
        let confirmationSettled = false;
        confirmationOperation.then(() => { confirmationSettled = true; });
        confirmationApi.invalidatePageLifecycle(confirmationPage);
        confirmationApi.beginPageLifecycle(confirmationPage);
        assert.equal(confirmationPage[testCase.flag], false, `${testCase.method} clears its flag when pagehide cancels confirmation`);
        assert.equal(confirmationButton.disabled, false, `${testCase.method} restores its button when pagehide cancels confirmation`);
        assert.equal(confirmationPage._huePageRequests[testCase.key], undefined, `${testCase.method} has no request after pagehide cancels confirmation`);
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(confirmationSettled, true, `${testCase.method} settles when pagehide closes the confirmation lifecycle`);
        confirmation(true);
        assert.equal(confirmationHarness.requests.length, 0, `${testCase.method} ignores a stale confirmation callback`);
        await confirmationOperation;

        const staleHarness = makeHarness();
        const stalePage = staleHarness.page;
        const staleApi = staleHarness.api;
        const staleState = {
            runtimeLoads: 0,
            historyLoads: 0,
            status: stalePage.querySelector(testCase.status),
            button: stalePage.querySelector(testCase.button)
        };
        testCase.reload(staleApi, staleState);
        let staleConfirmation;
        staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
        const staleOperation = staleApi[testCase.method](stalePage);
        staleConfirmation(true);
        assert.equal(staleHarness.requests.length, 1, `${testCase.method} starts one confirmed request`);
        assert.equal(staleHarness.requests[0].options.type, "DELETE", `${testCase.method} uses DELETE`);
        assert.equal(staleHarness.requests[0].options.url, testCase.route, `${testCase.method} targets the history endpoint`);
        assert.ok(stalePage._huePageRequests[testCase.key], `${testCase.method} tracks its destructive request`);
        assert.equal(stalePage[testCase.flag], true, `${testCase.method} marks itself busy while the request is pending`);
        assert.equal(staleState.button.disabled, true, `${testCase.method} keeps its button disabled while the request is pending`);
        staleState.status.textContent = "unchanged after pagehide";
        staleApi.invalidatePageLifecycle(stalePage);
        assert.equal(staleHarness.requests[0].promise.aborted, true, `pagehide aborts ${testCase.method}`);
        assert.equal(stalePage._huePageRequests[testCase.key], undefined, `pagehide removes ${testCase.method} request state`);
        assert.equal(stalePage[testCase.flag], false, `pagehide clears ${testCase.method} busy state`);
        assert.equal(staleState.button.disabled, false, `pagehide restores the ${testCase.method} button`);
        staleHarness.requests[0].resolve({ ClearedCount: 9 });
        await staleOperation;
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(staleState.status.textContent, "unchanged after pagehide", `stale ${testCase.method} cannot write status`);
        assert.equal(staleState.runtimeLoads, 0, `stale ${testCase.method} cannot refresh runtime status`);
        assert.equal(staleState.historyLoads, 0, `stale ${testCase.method} cannot refresh history`);

        const currentHarness = makeHarness();
        const currentPage = currentHarness.page;
        const currentApi = currentHarness.api;
        const currentState = {
            runtimeLoads: 0,
            historyLoads: 0,
            status: currentPage.querySelector(testCase.status),
            button: currentPage.querySelector(testCase.button)
        };
        testCase.reload(currentApi, currentState);
        let currentConfirmation;
        let currentConfirmationCalls = 0;
        currentHarness.dashboard.confirm = (_message, _title, callback) => {
            currentConfirmationCalls += 1;
            currentConfirmation = callback;
        };
        const currentOperation = currentApi[testCase.method](currentPage);
        currentConfirmation(true);
        assert.equal(currentHarness.requests.length, 1, `current ${testCase.method} starts one request`);
        const duplicateWhilePending = currentApi[testCase.method](currentPage);
        await duplicateWhilePending;
        assert.equal(currentConfirmationCalls, 1, `pending ${testCase.method} suppresses a duplicate request`);
        assert.equal(currentHarness.requests.length, 1, `pending ${testCase.method} keeps one request in flight`);
        currentHarness.requests[0].resolve(testCase.response);
        await currentOperation;
        assert.equal(currentState.status.textContent, testCase.successText, `current ${testCase.method} reports success`);
        assert.equal(currentState.runtimeLoads, testCase.method === "clearSceneScheduleHistory" ? 1 : 0, `current ${testCase.method} refreshes runtime status when applicable`);
        assert.equal(currentState.historyLoads, 1, `current ${testCase.method} refreshes history`);
        assert.equal(currentPage[testCase.flag], false, `current ${testCase.method} clears its busy state`);
        assert.equal(currentState.button.disabled, false, `current ${testCase.method} does not leave its button stuck disabled`);
        assert.equal(currentPage._huePageRequests[testCase.key], undefined, `current ${testCase.method} removes its settled request state`);

        if (testCase.method === "clearSessionHistory") {
            const raceHarness = makeHarness();
            const racePage = raceHarness.page;
            const raceApi = raceHarness.api;
            const raceContainer = racePage.querySelector("#runtimeSessionHistory");
            const raceRefreshButton = racePage.querySelector("#refreshSessionHistoryBtn");
            let raceConfirmation;
            raceHarness.dashboard.confirm = (_message, _title, callback) => { raceConfirmation = callback; };
            const staleHistoryLoad = raceApi.loadSessionHistory(racePage);
            const staleHistoryRequest = raceHarness.requests[0];
            assert.equal(raceHarness.requests.length, 1, "session history stale-race starts with one GET");
            const raceClearOperation = raceApi.clearSessionHistory(racePage);
            raceConfirmation(true);
            assert.equal(staleHistoryRequest.promise.aborted, true, "session history clear aborts the pending history GET");
            assert.equal(racePage._hueSessionHistoryLoading, false, "session history clear resets the canceled GET loading state");
            assert.equal(raceRefreshButton.disabled, false, "session history clear restores the refresh button after canceling the GET");
            assert.equal(raceHarness.requests.length, 2, "session history stale-race starts DELETE after canceling the GET");
            raceHarness.requests[1].resolve({ ClearedCount: 0 });
            staleHistoryRequest.resolve({ Sessions: [{ UserName: "stale-private-row" }] });
            await staleHistoryLoad;
            await new Promise(resolve => setImmediate(resolve));
            assert.equal(raceHarness.requests.length, 3, "session history clear forces a fresh GET after DELETE success");
            assert.equal(raceHarness.requests[2].options.type, "GET", "session history post-clear refresh uses GET");
            assert.equal(raceHarness.requests[2].options.url, "HueSync/History?limit=20&outcome=Stopped", "session history post-clear refresh preserves the active filter");
            assert.equal(raceContainer.textContent.includes("stale-private-row"), false, "stale session history completion cannot repopulate cleared rows");
            raceHarness.requests[2].resolve({ Sessions: [] });
            await raceClearOperation;
            assert.equal(raceContainer.textContent, "No completed Hue sessions matching 'Stopped' have been recorded since the service started.", "session history clear renders the fresh empty result");
            assert.equal(racePage._hueSessionHistoryClearing, false, "session history stale-race clears its busy state");
            assert.equal(racePage.querySelector(testCase.button).disabled, false, "session history stale-race does not leave the clear button stuck disabled");
        }
    }
}

async function testRuntimeStopLifecycleGuards() {
    const cases = [
        {
            method: "stopRuntimeSync",
            arguments: [],
            route: "HueSync/Stop"
        },
        {
            method: "stopRuntimeSession",
            arguments: ["play-session-1"],
            route: "HueSync/Stop?playSessionId=play-session-1"
        }
    ];

    for (const testCase of cases) {
        const harness = makeHarness();
        const { page, api, requests, dashboard } = harness;
        let statusLoads = 0;
        api.loadRuntimeStatus = () => {
            statusLoads += 1;
            return Promise.resolve();
        };

        const operation = api[testCase.method](page, ...testCase.arguments);
        assert.ok(operation && typeof operation.then === "function", `${testCase.method} returns a promise`);
        assert.equal(requests.length, 1, `${testCase.method} starts one request`);
        assert.equal(requests[0].options.type, "POST", `${testCase.method} uses POST`);
        assert.equal(requests[0].options.url, testCase.route, `${testCase.method} targets the selected stop route`);
        assert.equal(page._hueRuntimeStopInFlight, true, `${testCase.method} marks the stop busy`);

        api.invalidatePageLifecycle(page);
        assert.equal(page._huePageActive, false, `${testCase.method} invalidates the page lifecycle`);
        assert.equal(requests[0].promise.aborted, false, `${testCase.method} preserves the explicit server-side stop request`);
        requests[0].resolve({ State: "Stopped" });
        await operation;

        assert.equal(statusLoads, 0, `${testCase.method} cannot restart runtime polling after pagehide`);
        assert.equal(dashboard.alerts.length, 0, `${testCase.method} suppresses stale pagehide alerts`);
        assert.equal(page._hueRuntimeStopInFlight, false, `${testCase.method} clears stop state after completion`);
    }
}

async function testEditMappingLifecycleGuards() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    for (const method of [
        "toggleMappingSyncFields",
        "toggleMappingManualArea",
        "refreshMappingDeviceRoutes",
        "loadMappingAreas",
        "showStoredMappingArea"
    ]) {
        api[method] = () => {};
    }

    const first = api.editUserMapping(page, "user-one", "mapping-one");
    assert.equal(requests.length, 1, "first mapping edit starts one request");
    const second = api.editUserMapping(page, "user-two", "mapping-two");
    assert.equal(requests.length, 2, "second mapping edit starts one request");
    assert.equal(requests[0].promise.aborted, true, "second mapping edit aborts the superseded request");

    page.querySelector("#mappingFormLegend").textContent = "unchanged while stale";
    requests[0].resolve([{
        MappingId: "mapping-one",
        UserId: "user-one",
        UserName: "User One",
        SyncEnabled: false
    }]);
    await first;
    assert.equal(
        page.querySelector("#mappingFormLegend").textContent,
        "unchanged while stale",
        "superseded mapping edit cannot overwrite the form");

    requests[1].resolve([{
        MappingId: "mapping-two",
        UserId: "user-two",
        UserName: "User Two",
        SyncEnabled: false
    }]);
    await second;
    assert.equal(
        page.querySelector("#mappingFormLegend").textContent,
        "Edit User Mapping",
        "current mapping edit populates the form");

    page.querySelector("#mappingFormLegend").textContent = "unchanged after pagehide";
    const afterPagehide = api.editUserMapping(page, "user-three", "mapping-three");
    assert.equal(requests.length, 3, "pagehide mapping edit starts one request");
    api.invalidatePageLifecycle(page);
    assert.equal(requests[2].promise.aborted, true, "pagehide aborts the mapping edit request");
    requests[2].resolve([{
        MappingId: "mapping-three",
        UserId: "user-three",
        UserName: "User Three",
        SyncEnabled: false
    }]);
    await afterPagehide;
    assert.equal(
        page.querySelector("#mappingFormLegend").textContent,
        "unchanged after pagehide",
        "invalidated mapping edit cannot overwrite the hidden page");
}

async function testUserMappingSaveLifecycleGuards() {
    const staleHarness = makeHarness();
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    const staleUserSelect = stalePage.querySelector("#mappingUserSelect");
    staleUserSelect.value = "user-one";
    staleUserSelect.selectedOptions = [{ dataset: { userName: "User One" } }];
    stalePage.querySelector("#mappingAreaSelect").selectedOptions = [];
    stalePage.querySelector("#mappingSyncEnabled").checked = false;
    const staleFollowUps = [];
    staleApi.loadUserMappings = () => { staleFollowUps.push("mappings"); };
    staleApi.resetMappingForm = () => { staleFollowUps.push("reset"); };

    const staleOperation = staleApi.addUserMapping();
    assert.ok(staleOperation && typeof staleOperation.then === "function", "user-mapping save returns a promise");
    assert.equal(staleHarness.requests.length, 1, "user-mapping save starts one tracked request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "user-mapping save uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/UserMappings", "user-mapping save uses the mapping endpoint");
    assert.deepEqual(
        JSON.parse(staleHarness.requests[0].options.data),
        {
            MappingId: "",
            UserId: "user-one",
            UserName: "User One",
            SyncEnabled: false,
            HueBridgeIp: "",
            HueAppKey: "",
            HueClientKey: "",
            EntertainmentAreaId: "",
            EntertainmentAreaName: "",
            DeviceTargets: [],
            UseCinemaModeOverride: null,
            PlaybackMediaFilterOverride: null,
            AudioSensitivityPercentOverride: null,
            AudioNoiseGatePercentOverride: null,
            AudioLowFrequencyHzOverride: null,
            AudioMidFrequencyHzOverride: null,
            AudioHighFrequencyHzOverride: null,
            AudioLowGainPercentOverride: null,
            AudioMidGainPercentOverride: null,
            AudioHighGainPercentOverride: null,
            AudioResponseSmoothingPercentOverride: null,
            AudioBandSpreadPercentOverride: null,
            AudioBeatPulsePercentOverride: null,
            AudioBeatPulseDecayPercentOverride: null,
            AudioBeatPulseThresholdPercentOverride: null,
            AudioColorPaletteOverride: null,
            AudioSpatialModeOverride: null,
            AudioChannelModeOverride: null,
            BrightnessDimLevelOverride: null,
            PauseBehaviorOverride: null,
            RestoreLightStateOverride: null,
            BrightnessBoostOverride: null,
            RedGainOverride: null,
            GreenGainOverride: null,
            BlueGainOverride: null,
            ColorSaturationOverride: null,
            HueShiftDegreesOverride: null,
            OutputBrightnessPercentOverride: null,
            GammaCorrectionOverride: null,
            ContrastPercentOverride: null,
            ColorTemperatureKelvinOverride: null,
            BlackoutThresholdOverride: null,
            BlackoutBehaviorOverride: null,
            ColorChangeThresholdOverride: null,
            TargetFpsOverride: null,
            FrameResolutionOverride: null,
            VideoScalingModeOverride: null,
            VideoDeinterlaceModeOverride: null,
            SamplingBreadthPercentOverride: null,
            SamplingModeOverride: null,
            SpatialOrientationOverride: null,
            ColorSmoothingPercentOverride: null,
            UseGpuOverride: null,
            CustomFfmpegFlagsOverride: null,
            FfmpegStallTimeoutSecondsOverride: null,
            NetworkRetryAttemptsOverride: null,
            ChannelIdsOverride: null
        },
        "user-mapping save sends the complete credential-free mapping payload"
    );
    assert.equal(stalePage._hueUserMappingSaving, true, "user-mapping save marks the form busy");
    assert.equal(stalePage.querySelector("#addMappingBtn").disabled, true, "user-mapping save disables its button");

    const duplicate = staleApi.addUserMapping();
    assert.ok(duplicate && typeof duplicate.then === "function", "duplicate user-mapping save returns a settled no-op promise");
    assert.equal(staleHarness.requests.length, 1, "duplicate user-mapping save does not submit twice");

    stalePage.querySelector("#mappingBridgeStatus").textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts the user-mapping save");
    assert.equal(stalePage._huePageRequests.userMappingSave, undefined, "pagehide removes the user-mapping save record");
    assert.equal(stalePage._hueUserMappingSaving, false, "pagehide clears the user-mapping busy state");
    assert.equal(stalePage.querySelector("#addMappingBtn").disabled, false, "pagehide restores the user-mapping button");
    staleHarness.requests[0].resolve({ message: "stale save" });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(stalePage.querySelector("#mappingBridgeStatus").textContent, "unchanged after pagehide", "stale save cannot update the hidden page");
    assert.deepEqual(staleFollowUps, [], "stale save cannot reload mappings or reset a new draft");
    assert.deepEqual(staleHarness.dashboard.alerts, [], "stale save cannot alert after pagehide");

    const currentHarness = makeHarness();
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    const currentUserSelect = currentPage.querySelector("#mappingUserSelect");
    currentUserSelect.value = "user-two";
    currentUserSelect.selectedOptions = [{ dataset: { userName: "User Two" } }];
    currentPage.querySelector("#mappingAreaSelect").selectedOptions = [];
    currentPage.querySelector("#mappingSyncEnabled").checked = false;
    const currentFollowUps = [];
    currentApi.loadUserMappings = () => { currentFollowUps.push("mappings"); };
    currentApi.resetMappingForm = () => { currentFollowUps.push("reset"); };
    const currentOperation = currentApi.addUserMapping();
    currentHarness.requests[0].resolve({ message: "saved" });
    await currentOperation;
    assert.deepEqual(currentFollowUps, ["mappings", "reset"], "current save reloads mappings and resets the form");
    assert.equal(currentHarness.dashboard.alerts.length, 1, "current save reports success");
    assert.equal(currentPage._hueUserMappingSaving, false, "current save clears the busy state");
    assert.equal(currentPage.querySelector("#addMappingBtn").disabled, false, "current save restores the button");
}

async function testUserMappingDeleteLifecycleGuards() {
    const confirmationHarness = makeHarness();
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    const confirmationButton = confirmationPage.querySelector("#mappingDeleteButton");
    let confirmation;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => { confirmation = callback; };
    const confirmationOperation = confirmationApi.deleteUserMapping(
        confirmationPage,
        "user-one",
        "mapping-one",
        confirmationButton
    );
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "user-mapping delete returns a promise");
    assert.equal(confirmationHarness.requests.length, 0, "user-mapping delete waits for confirmation before mutating configuration");
    assert.equal(confirmationButton.disabled, true, "pending user-mapping delete disables its row button");
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmationApi.beginPageLifecycle(confirmationPage);
    assert.equal(confirmationButton.disabled, false, "pagehide restores the pending user-mapping delete button");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "a stale user-mapping delete confirmation cannot start a request after pagehide");
    await confirmationOperation;
    assert.deepEqual(confirmationHarness.dashboard.alerts, [], "a stale delete confirmation cannot alert");

    const staleHarness = makeHarness();
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    const staleButton = stalePage.querySelector("#mappingDeleteButton");
    const staleFollowUps = [];
    staleApi.loadUserMappings = () => { staleFollowUps.push("mappings"); };
    staleApi.resetMappingForm = () => { staleFollowUps.push("reset"); };
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.deleteUserMapping(stalePage, "user-one", "mapping-one", staleButton);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed user-mapping delete starts one request");
    assert.equal(staleHarness.requests[0].options.type, "DELETE", "user-mapping delete uses DELETE");
    assert.equal(
        staleHarness.requests[0].options.url,
        "HueSync/UserMappings/user-one?mappingId=mapping-one",
        "user-mapping delete scopes the request to the selected mapping"
    );
    assert.equal(staleHarness.requests[0].options.dataType, "json", "user-mapping delete accepts the API response safely");
    assert.ok(stalePage._huePageRequests.userMappingDelete, "user-mapping delete is tracked by the page lifecycle");
    assert.equal(stalePage._hueUserMappingDeleting, true, "user-mapping delete marks the page busy");
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight user-mapping delete");
    assert.equal(stalePage._huePageRequests.userMappingDelete, undefined, "pagehide removes the user-mapping delete request record");
    assert.equal(stalePage._hueUserMappingDeleting, false, "pagehide clears the user-mapping delete busy state");
    assert.equal(staleButton.disabled, false, "pagehide restores the user-mapping delete button");
    staleHarness.requests[0].resolve({ message: "stale delete" });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(staleFollowUps, [], "an invalidated delete cannot reload mappings or reset a new draft");
    assert.deepEqual(staleHarness.dashboard.alerts, [], "an invalidated delete cannot alert after pagehide");

    const currentHarness = makeHarness();
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    const currentButton = currentPage.querySelector("#mappingDeleteButton");
    const currentFollowUps = [];
    currentApi.mappingEditingMappingId = "mapping-two";
    currentApi.loadUserMappings = () => { currentFollowUps.push("mappings"); };
    currentApi.resetMappingForm = () => { currentFollowUps.push("reset"); };
    currentHarness.dashboard.confirm = (_message, _title, callback) => { confirmation = callback; };
    const currentOperation = currentApi.deleteUserMapping(currentPage, "user-two", "mapping-two", currentButton);
    confirmation(true);
    assert.equal(currentHarness.requests.length, 1, "current user-mapping delete starts one request");
    assert.equal(currentButton.disabled, true, "current user-mapping delete keeps its row button disabled while pending");
    const duplicate = currentApi.deleteUserMapping(currentPage, "user-two", "mapping-two", currentButton);
    await duplicate;
    assert.equal(currentHarness.requests.length, 1, "duplicate user-mapping delete does not submit twice");
    currentHarness.requests[0].resolve({ message: "deleted" });
    await currentOperation;
    assert.deepEqual(currentFollowUps, ["mappings", "reset"], "current delete reloads mappings and resets the matching edit");
    assert.equal(currentHarness.dashboard.alerts.length, 1, "current delete reports success");
    assert.equal(currentPage._hueUserMappingDeleting, false, "current delete clears its busy state");
    assert.equal(currentButton.disabled, false, "current delete restores its row button");
    assert.equal(currentPage._huePageRequests.userMappingDelete, undefined, "current delete removes its settled lifecycle record");
}

async function testUserMappingCleanupLifecycleGuards() {
    const report = {
        ReportVersion: "report-one",
        Mappings: [{ MappingId: "mapping-one", Status: "MissingUser" }]
    };

    const confirmationHarness = makeHarness();
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => { confirmation = callback; };
    const confirmationOperation = confirmationApi.cleanupStaleUserMappings(confirmationPage);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "stale-mapping cleanup returns a promise");
    assert.equal(confirmationHarness.requests.length, 1, "stale-mapping cleanup starts one reconciliation report request");
    assert.equal(confirmationHarness.requests[0].options.type, "GET", "stale-mapping cleanup report uses GET");
    assert.equal(
        confirmationHarness.requests[0].options.url,
        "HueSync/UserMappings/Reconcile",
        "stale-mapping cleanup report uses the reconciliation endpoint");
    assert.equal(confirmationPage._hueUserMappingCleanupRunning, true, "stale-mapping cleanup marks the page busy while reviewing the report");
    confirmationHarness.requests[0].resolve(report);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(typeof confirmation, "function", "a current stale-mapping report asks for confirmation");
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    assert.equal(confirmationHarness.requests[0].promise.aborted, true, "pagehide aborts the stale-mapping report request");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 1, "a stale cleanup confirmation cannot start a destructive request");
    await confirmationOperation;

    const staleHarness = makeHarness();
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    const staleFollowUps = [];
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    staleApi.loadUserMappings = () => { staleFollowUps.push("mappings"); };
    const staleOperation = staleApi.cleanupStaleUserMappings(stalePage);
    staleHarness.requests[0].resolve(report);
    await new Promise(resolve => setImmediate(resolve));
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 2, "confirmed cleanup starts one destructive request");
    assert.equal(staleHarness.requests[1].options.type, "POST", "stale-mapping cleanup uses POST");
    assert.equal(staleHarness.requests[1].options.url, "HueSync/UserMappings/Cleanup", "stale-mapping cleanup uses the cleanup endpoint");
    assert.deepEqual(
        JSON.parse(staleHarness.requests[1].options.data),
        { mappingIds: ["mapping-one"], expectedReportVersion: "report-one" },
        "stale-mapping cleanup scopes the request to the reviewed row and report version");
    assert.ok(stalePage._huePageRequests.userMappingCleanup, "stale-mapping cleanup is tracked by the page lifecycle");
    stalePage.querySelector("#userMappingReconcileStatus").textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[1].promise.aborted, true, "pagehide aborts an in-flight stale-mapping cleanup");
    assert.equal(stalePage._huePageRequests.userMappingCleanup, undefined, "pagehide removes stale-mapping cleanup request state");
    assert.equal(stalePage._hueUserMappingCleanupRunning, false, "pagehide clears stale-mapping cleanup busy state");
    staleHarness.requests[1].resolve({ DeletedCount: 1 });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(staleFollowUps, [], "invalidated cleanup cannot reload user mappings");
    assert.equal(
        stalePage.querySelector("#userMappingReconcileStatus").textContent,
        "unchanged after pagehide",
        "invalidated cleanup cannot update hidden-page status");

    const currentHarness = makeHarness();
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    const currentFollowUps = [];
    let currentConfirmation;
    currentHarness.dashboard.confirm = (_message, _title, callback) => { currentConfirmation = callback; };
    currentApi.loadUserMappings = () => { currentFollowUps.push("mappings"); };
    const currentOperation = currentApi.cleanupStaleUserMappings(currentPage);
    currentHarness.requests[0].resolve(report);
    await new Promise(resolve => setImmediate(resolve));
    currentConfirmation(true);
    assert.equal(currentHarness.requests.length, 2, "current cleanup starts one destructive request");
    currentHarness.requests[1].resolve({ DeletedCount: 1 });
    await currentOperation;
    assert.deepEqual(currentFollowUps, ["mappings"], "current cleanup reloads mappings after success");
    assert.equal(
        currentPage.querySelector("#userMappingReconcileStatus").textContent,
        "Deleted 1 stale mapping row(s). Duplicate and referenced rows were protected.",
        "current cleanup reports the deleted row count");
    assert.equal(currentPage._hueUserMappingCleanupRunning, false, "current cleanup clears its busy state");
    assert.equal(currentPage.querySelector("#cleanupStaleUserMappingsBtn").disabled, false, "current cleanup restores its button");
    assert.equal(currentPage._huePageRequests.userMappingCleanup, undefined, "current cleanup removes its settled lifecycle record");
}

function configureUserMappingBulkDeleteHarness(harness) {
    const { page } = harness;
    const bulkSelect = page.querySelector("#userMappingBulkSelect");
    bulkSelect.options = [
        { value: "user-one", selected: true, dataset: { mappingid: "mapping-one" } },
        { value: "user-two", selected: true, dataset: { mappingid: "mapping-two" } }
    ];
    return {
        bulkSelect,
        selectAllButton: page.querySelector("#selectAllUserMappingsBtn"),
        clearButton: page.querySelector("#clearSelectedUserMappingsBtn"),
        enableButton: page.querySelector("#enableSelectedUserMappingsBtn"),
        disableButton: page.querySelector("#disableSelectedUserMappingsBtn"),
        button: page.querySelector("#deleteSelectedUserMappingsBtn"),
        status: page.querySelector("#userMappingBulkStatus"),
        mappingLoads: 0,
        buttonUpdates: 0
    };
}

async function testUserMappingBulkDeleteLifecycleGuards() {
    const confirmationHarness = makeHarness();
    const confirmationState = configureUserMappingBulkDeleteHarness(confirmationHarness);
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    let confirmationCalls = 0;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
        confirmationCalls += 1;
        confirmation = callback;
    };
    const confirmationOperation = confirmationApi.deleteUserMappingsBulk(confirmationPage);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "bulk user-mapping delete returns a promise");
    assert.equal(confirmationHarness.requests.length, 0, "bulk user-mapping delete waits for confirmation before mutating configuration");
    assert.equal(confirmationCalls, 1, "bulk user-mapping delete asks for one confirmation");
    assert.equal(confirmationState.bulkSelect.disabled, true, "pending bulk user-mapping delete disables selection");
    assert.equal(confirmationState.selectAllButton.disabled, true, "pending bulk user-mapping delete disables select all");
    assert.equal(confirmationState.clearButton.disabled, true, "pending bulk user-mapping delete disables clear selection");
    assert.equal(confirmationState.enableButton.disabled, true, "pending bulk user-mapping delete disables enable");
    assert.equal(confirmationState.disableButton.disabled, true, "pending bulk user-mapping delete disables disable");
    assert.equal(confirmationState.button.disabled, true, "pending bulk user-mapping delete disables its button");
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmationApi.beginPageLifecycle(confirmationPage);
    assert.equal(confirmationPage._hueUserMappingBulkMutation, null, "pagehide releases the pending bulk user-mapping delete owner");
    assert.equal(confirmationState.bulkSelect.disabled, false, "pagehide restores bulk user-mapping selection");
    assert.equal(confirmationState.button.disabled, false, "pagehide restores the pending bulk user-mapping delete button");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "stale bulk user-mapping delete confirmation cannot start a request after pagehide");
    await confirmationOperation;

    const staleHarness = makeHarness();
    const staleState = configureUserMappingBulkDeleteHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadUserMappings = () => {
        staleState.mappingLoads += 1;
        return Promise.resolve();
    };
    staleApi.updateUserMappingBulkButtons = () => {
        staleState.buttonUpdates += 1;
    };
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.deleteUserMappingsBulk(stalePage);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed bulk user-mapping delete starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "bulk user-mapping delete uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/UserMappings/BulkDelete", "bulk user-mapping delete targets the bulk endpoint");
    assert.deepEqual(
        JSON.parse(staleHarness.requests[0].options.data),
        { mappingIds: ["mapping-one", "mapping-two"] },
        "bulk user-mapping delete sends stable selected mapping IDs"
    );
    assert.ok(stalePage._huePageRequests.userMappingBulkDelete, "bulk user-mapping delete is tracked by the page lifecycle");
    assert.equal(stalePage._hueUserMappingBulkDeleting, true, "bulk user-mapping delete marks the page busy");
    assert.equal(staleState.bulkSelect.disabled, true, "bulk user-mapping delete disables selection while pending");
    assert.equal(staleState.selectAllButton.disabled, true, "bulk user-mapping delete disables select all while pending");
    assert.equal(staleState.clearButton.disabled, true, "bulk user-mapping delete disables clear selection while pending");
    assert.equal(staleState.enableButton.disabled, true, "bulk user-mapping delete disables enable while pending");
    assert.equal(staleState.disableButton.disabled, true, "bulk user-mapping delete disables disable while pending");
    assert.equal(staleState.button.disabled, true, "bulk user-mapping delete keeps its button disabled while pending");
    staleState.status.textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight bulk user-mapping delete");
    assert.equal(stalePage._huePageRequests.userMappingBulkDelete, undefined, "pagehide removes the bulk user-mapping delete request record");
    assert.equal(stalePage._hueUserMappingBulkMutation, null, "pagehide releases the bulk user-mapping delete owner");
    assert.equal(stalePage._hueUserMappingBulkDeleting, false, "pagehide clears bulk user-mapping delete state");
    assert.equal(staleState.button.disabled, false, "pagehide restores the bulk user-mapping delete button");
    staleState.bulkSelect.options = [
        { value: "new-user", selected: true, dataset: { mappingid: "new-mapping" } }
    ];
    staleHarness.requests[0].resolve({ deletedCount: 2 });
    await staleOperation;
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale bulk user-mapping delete cannot write hidden-page status");
    assert.equal(staleState.bulkSelect.options[0].selected, true, "stale bulk user-mapping delete cannot clear a reused page selection");
    assert.equal(staleState.mappingLoads, 0, "stale bulk user-mapping delete cannot reload mappings");
    assert.equal(staleState.buttonUpdates, 0, "stale bulk user-mapping delete cannot update current-page controls");
    assert.equal(staleHarness.dashboard.alerts.length, 0, "stale bulk user-mapping delete cannot show an alert");

    const currentHarness = makeHarness();
    const currentState = configureUserMappingBulkDeleteHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadUserMappings = () => {
        currentState.mappingLoads += 1;
        return Promise.resolve();
    };
    const updateCurrentBulkButtons = currentApi.updateUserMappingBulkButtons;
    currentApi.updateUserMappingBulkButtons = page => {
        currentState.buttonUpdates += 1;
        updateCurrentBulkButtons(page);
    };
    let currentConfirmation;
    let currentConfirmationCalls = 0;
    currentHarness.dashboard.confirm = (_message, _title, callback) => {
        currentConfirmationCalls += 1;
        currentConfirmation = callback;
    };
    const currentOperation = currentApi.deleteUserMappingsBulk(currentPage);
    const duplicateBeforeConfirmation = currentApi.deleteUserMappingsBulk(currentPage);
    assert.equal(currentConfirmationCalls, 1, "duplicate bulk user-mapping delete does not open another confirmation");
    assert.equal(currentHarness.requests.length, 0, "duplicate bulk user-mapping delete does not submit before confirmation");
    await duplicateBeforeConfirmation;
    currentConfirmation(true);
    assert.equal(currentState.bulkSelect.disabled, true, "confirmed bulk user-mapping delete disables selection while pending");
    assert.equal(currentState.selectAllButton.disabled, true, "confirmed bulk user-mapping delete disables select all while pending");
    assert.equal(currentState.clearButton.disabled, true, "confirmed bulk user-mapping delete disables clear selection while pending");
    assert.equal(currentState.enableButton.disabled, true, "confirmed bulk user-mapping delete disables enable while pending");
    assert.equal(currentState.disableButton.disabled, true, "confirmed bulk user-mapping delete disables disable while pending");
    currentApi.clearSelectedUserMappings(currentPage);
    currentApi.selectAllUserMappings(currentPage);
    assert.equal(currentState.bulkSelect.options.every(option => option.selected === true), true, "locked bulk user-mapping delete ignores selection helper mutations");
    const duplicateEnabledWhilePending = currentApi.setUserMappingsEnabledBulk(currentPage, true);
    assert.ok(duplicateEnabledWhilePending && typeof duplicateEnabledWhilePending.then === "function", "cross-action bulk mutation returns a settled no-op while delete is pending");
    const duplicateWhilePending = currentApi.deleteUserMappingsBulk(currentPage);
    assert.ok(duplicateWhilePending && typeof duplicateWhilePending.then === "function", "duplicate pending bulk user-mapping delete returns a settled no-op");
    assert.equal(currentHarness.requests.length, 1, "pending bulk user-mapping delete keeps one request in flight");
    currentHarness.requests[0].resolve({ deletedCount: 2 });
    await Promise.all([currentOperation, duplicateWhilePending, duplicateEnabledWhilePending]);
    assert.equal(currentState.status.textContent, "Deleted 2 user mapping(s).", "current bulk user-mapping delete reports success");
    assert.equal(currentState.bulkSelect.options.every(option => option.selected === false), true, "current bulk user-mapping delete clears the selected rows");
    assert.equal(currentState.mappingLoads, 1, "current bulk user-mapping delete reloads mappings once");
    assert.equal(currentPage._hueUserMappingBulkDeleting, false, "current bulk user-mapping delete clears its busy state");
    assert.equal(currentState.button.disabled, true, "current bulk user-mapping delete keeps its button disabled with no selection");
    assert.equal(currentState.enableButton.disabled, true, "current bulk user-mapping delete keeps enable disabled with no selection");
    assert.equal(currentState.disableButton.disabled, true, "current bulk user-mapping delete keeps disable disabled with no selection");
    assert.equal(currentState.buttonUpdates, 1, "current bulk user-mapping delete refreshes current-page controls once");
    assert.equal(currentPage._hueUserMappingBulkMutation, null, "current bulk user-mapping delete releases its owner");
    assert.equal(currentPage._huePageRequests.userMappingBulkDelete, undefined, "current bulk user-mapping delete removes its settled lifecycle record");
}

function configureUserMappingBulkEnabledHarness(harness) {
    const { page } = harness;
    const bulkSelect = page.querySelector("#userMappingBulkSelect");
    bulkSelect.options = [
        { value: "user-one", selected: true, dataset: { mappingid: "mapping-one" } },
        { value: "user-two", selected: true, dataset: { mappingid: "mapping-two" } }
    ];
    return {
        bulkSelect,
        selectAllButton: page.querySelector("#selectAllUserMappingsBtn"),
        clearButton: page.querySelector("#clearSelectedUserMappingsBtn"),
        enableButton: page.querySelector("#enableSelectedUserMappingsBtn"),
        disableButton: page.querySelector("#disableSelectedUserMappingsBtn"),
        deleteButton: page.querySelector("#deleteSelectedUserMappingsBtn"),
        status: page.querySelector("#userMappingBulkStatus"),
        mappingLoads: 0,
        buttonUpdates: 0
    };
}

async function testUserMappingBulkEnabledLifecycleGuards() {
    const confirmationHarness = makeHarness();
    const confirmationState = configureUserMappingBulkEnabledHarness(confirmationHarness);
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    let confirmationCalls = 0;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
        confirmationCalls += 1;
        confirmation = callback;
    };
    const confirmationOperation = confirmationApi.setUserMappingsEnabledBulk(confirmationPage, false);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "bulk user-mapping enabled update returns a promise");
    assert.equal(confirmationHarness.requests.length, 0, "bulk user-mapping enabled update waits for confirmation before mutating configuration");
    assert.equal(confirmationCalls, 1, "bulk user-mapping enabled update asks for one confirmation");
    assert.equal(confirmationState.bulkSelect.disabled, true, "pending bulk user-mapping enabled update disables selection");
    assert.equal(confirmationState.selectAllButton.disabled, true, "pending bulk user-mapping enabled update disables select all");
    assert.equal(confirmationState.clearButton.disabled, true, "pending bulk user-mapping enabled update disables clear selection");
    assert.equal(confirmationState.enableButton.disabled, true, "pending bulk user-mapping enabled update disables the enable button");
    assert.equal(confirmationState.disableButton.disabled, true, "pending bulk user-mapping enabled update disables the disable button");
    assert.equal(confirmationState.deleteButton.disabled, true, "pending bulk user-mapping enabled update disables delete");
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmationApi.beginPageLifecycle(confirmationPage);
    assert.equal(confirmationPage._hueUserMappingBulkMutation, null, "pagehide releases the pending bulk user-mapping enabled owner");
    assert.equal(confirmationState.bulkSelect.disabled, false, "pagehide restores bulk user-mapping selection");
    assert.equal(confirmationState.enableButton.disabled, false, "pagehide restores the pending bulk enabled enable button");
    assert.equal(confirmationState.disableButton.disabled, false, "pagehide restores the pending bulk enabled disable button");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "stale bulk user-mapping enabled confirmation cannot start a request after pagehide");
    await confirmationOperation;

    const staleHarness = makeHarness();
    const staleState = configureUserMappingBulkEnabledHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadUserMappings = () => {
        staleState.mappingLoads += 1;
        return Promise.resolve();
    };
    staleApi.updateUserMappingBulkButtons = () => {
        staleState.buttonUpdates += 1;
    };
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.setUserMappingsEnabledBulk(stalePage, false);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed bulk user-mapping enabled update starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "bulk user-mapping enabled update uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/UserMappings/BulkEnabled", "bulk user-mapping enabled update targets the bulk endpoint");
    assert.deepEqual(
        JSON.parse(staleHarness.requests[0].options.data),
        { mappingIds: ["mapping-one", "mapping-two"], syncEnabled: false },
        "bulk user-mapping enabled update sends stable selected mapping IDs and state"
    );
    assert.ok(stalePage._huePageRequests.userMappingBulkEnabled, "bulk user-mapping enabled update is tracked by the page lifecycle");
    assert.equal(stalePage._hueUserMappingBulkUpdating, true, "bulk user-mapping enabled update marks the page busy");
    assert.equal(staleState.bulkSelect.disabled, true, "bulk user-mapping enabled update disables selection while pending");
    assert.equal(staleState.selectAllButton.disabled, true, "bulk user-mapping enabled update disables select all while pending");
    assert.equal(staleState.clearButton.disabled, true, "bulk user-mapping enabled update disables clear selection while pending");
    assert.equal(staleState.enableButton.disabled, true, "bulk user-mapping enabled update keeps the enable button disabled while pending");
    assert.equal(staleState.disableButton.disabled, true, "bulk user-mapping enabled update keeps the disable button disabled while pending");
    assert.equal(staleState.deleteButton.disabled, true, "bulk user-mapping enabled update disables delete while pending");
    staleState.status.textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight bulk user-mapping enabled update");
    assert.equal(stalePage._huePageRequests.userMappingBulkEnabled, undefined, "pagehide removes the bulk enabled request record");
    assert.equal(stalePage._hueUserMappingBulkMutation, null, "pagehide releases the bulk user-mapping enabled owner");
    assert.equal(stalePage._hueUserMappingBulkUpdating, false, "pagehide clears bulk user-mapping enabled state");
    assert.equal(staleState.enableButton.disabled, false, "pagehide restores the bulk enabled enable button");
    assert.equal(staleState.disableButton.disabled, false, "pagehide restores the bulk enabled disable button");
    staleState.bulkSelect.options = [
        { value: "new-user", selected: true, dataset: { mappingid: "new-mapping" } }
    ];
    staleHarness.requests[0].resolve({ updatedCount: 2 });
    await staleOperation;
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale bulk enabled completion cannot write hidden-page status");
    assert.equal(staleState.bulkSelect.options[0].selected, true, "stale bulk enabled completion cannot clear a reused page selection");
    assert.equal(staleState.mappingLoads, 0, "stale bulk enabled completion cannot reload mappings");
    assert.equal(staleState.buttonUpdates, 0, "stale bulk enabled completion cannot update current-page controls");
    assert.equal(staleHarness.dashboard.alerts.length, 0, "stale bulk enabled completion cannot show an alert");

    const currentHarness = makeHarness();
    const currentState = configureUserMappingBulkEnabledHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadUserMappings = () => {
        currentState.mappingLoads += 1;
        return Promise.resolve();
    };
    const updateCurrentBulkButtons = currentApi.updateUserMappingBulkButtons;
    currentApi.updateUserMappingBulkButtons = page => {
        currentState.buttonUpdates += 1;
        updateCurrentBulkButtons(page);
    };
    let currentConfirmation;
    let currentConfirmationCalls = 0;
    currentHarness.dashboard.confirm = (_message, _title, callback) => {
        currentConfirmationCalls += 1;
        currentConfirmation = callback;
    };
    const currentOperation = currentApi.setUserMappingsEnabledBulk(currentPage, false);
    const duplicateBeforeConfirmation = currentApi.setUserMappingsEnabledBulk(currentPage, false);
    assert.equal(currentConfirmationCalls, 1, "duplicate bulk user-mapping enabled update does not open another confirmation");
    assert.equal(currentHarness.requests.length, 0, "duplicate bulk user-mapping enabled update does not submit before confirmation");
    await duplicateBeforeConfirmation;
    currentConfirmation(true);
    assert.equal(currentState.bulkSelect.disabled, true, "confirmed bulk user-mapping enabled update disables selection while pending");
    assert.equal(currentState.selectAllButton.disabled, true, "confirmed bulk user-mapping enabled update disables select all while pending");
    assert.equal(currentState.clearButton.disabled, true, "confirmed bulk user-mapping enabled update disables clear selection while pending");
    assert.equal(currentState.enableButton.disabled, true, "confirmed bulk user-mapping enabled update keeps enable disabled while pending");
    assert.equal(currentState.disableButton.disabled, true, "confirmed bulk user-mapping enabled update keeps disable disabled while pending");
    assert.equal(currentState.deleteButton.disabled, true, "confirmed bulk user-mapping enabled update keeps delete disabled while pending");
    currentApi.clearSelectedUserMappings(currentPage);
    currentApi.selectAllUserMappings(currentPage);
    assert.equal(currentState.bulkSelect.options.every(option => option.selected === true), true, "locked bulk user-mapping enabled update ignores selection helper mutations");
    const duplicateDeleteWhilePending = currentApi.deleteUserMappingsBulk(currentPage);
    assert.ok(duplicateDeleteWhilePending && typeof duplicateDeleteWhilePending.then === "function", "cross-action bulk mutation returns a settled no-op while enabled update is pending");
    const duplicateWhilePending = currentApi.setUserMappingsEnabledBulk(currentPage, false);
    assert.ok(duplicateWhilePending && typeof duplicateWhilePending.then === "function", "duplicate pending bulk enabled update returns a settled no-op");
    assert.equal(currentHarness.requests.length, 1, "pending bulk user-mapping enabled update keeps one request in flight");
    currentHarness.requests[0].resolve({ updatedCount: 2 });
    await Promise.all([currentOperation, duplicateWhilePending, duplicateDeleteWhilePending]);
    assert.equal(currentState.status.textContent, "Disabled 2 user mapping(s).", "current bulk user-mapping enabled update reports success");
    assert.equal(currentState.bulkSelect.options.every(option => option.selected === false), true, "current bulk enabled update clears the selected rows");
    assert.equal(currentState.mappingLoads, 1, "current bulk enabled update reloads mappings once");
    assert.equal(currentPage._hueUserMappingBulkUpdating, false, "current bulk enabled update clears its busy state");
    assert.equal(currentState.enableButton.disabled, true, "current bulk enabled update keeps enable disabled with no selection");
    assert.equal(currentState.disableButton.disabled, true, "current bulk enabled update keeps disable disabled with no selection");
    assert.equal(currentState.deleteButton.disabled, true, "current bulk enabled update keeps delete disabled with no selection");
    assert.equal(currentState.buttonUpdates, 1, "current bulk enabled update refreshes current-page controls once");
    assert.equal(currentPage._hueUserMappingBulkMutation, null, "current bulk user-mapping enabled update releases its owner");
    assert.equal(currentPage._huePageRequests.userMappingBulkEnabled, undefined, "current bulk enabled update removes its settled lifecycle record");
}

async function testConfigurationImportValidationLifecycleGuards() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    api.applyConfigurationImportCredentials = () => true;
    page._hueImportDocument = { Configuration: { HueBridgeIp: "bridge.local" } };

    const first = api.validateConfigurationImport(page);
    assert.ok(first && typeof first.then === "function", "import validation returns a promise");
    assert.equal(requests.length, 1, "first import validation starts one request");
    assert.equal(requests[0].options.type, "POST", "import validation uses POST");
    assert.equal(requests[0].options.url, "HueSync/Configuration/ValidateImport", "import validation uses the preflight endpoint");
    assert.equal(page.querySelector("#validateConfigurationImportBtn").disabled, true, "validation disables its button while pending");

    const second = api.validateConfigurationImport(page);
    assert.equal(requests.length, 2, "second import validation starts one replacement request");
    assert.equal(requests[0].promise.aborted, true, "second import validation aborts the superseded request");
    page.querySelector("#configurationPortabilityStatus").textContent = "current validation marker";
    requests[0].resolve({
        valid: true,
        canImport: true,
        message: "stale validation"
    });
    await first;
    assert.equal(
        page.querySelector("#configurationPortabilityStatus").textContent,
        "current validation marker",
        "superseded import validation cannot overwrite current status");
    assert.equal(page.querySelector("#applyConfigurationImportBtn").disabled, true, "superseded import validation cannot enable import");

    requests[1].resolve({
        valid: true,
        canImport: true,
        configurationVersion: "test-configuration-version",
        message: "Import is valid",
        totalMappings: 1,
        totalColorPresets: 2,
        totalScenePlaylists: 3,
        totalSceneSchedules: 4
    });
    await second;
    assert.match(
        page.querySelector("#configurationPortabilityStatus").textContent,
        /^Import is valid/,
        "current import validation reports its result");
    assert.equal(page.querySelector("#applyConfigurationImportBtn").disabled, false, "current valid import enables apply");

    page._hueImportDocument = { Configuration: { HueBridgeIp: "other-bridge.local" } };
    const afterPagehide = api.validateConfigurationImport(page);
    assert.equal(requests.length, 3, "page-scoped import validation starts one request");
    page.querySelector("#configurationPortabilityStatus").textContent = "unchanged after pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(requests[2].promise.aborted, true, "pagehide aborts import validation");
    assert.equal(page._hueImportDocument, null, "pagehide clears the pending import document");
    assert.equal(page.querySelector("#applyConfigurationImportBtn").disabled, true, "pagehide disables import approval");
    requests[2].resolve({ valid: true, canImport: true, message: "hidden stale validation" });
    await afterPagehide;
    assert.equal(
        page.querySelector("#configurationPortabilityStatus").textContent,
        "unchanged after pagehide",
        "invalidated import validation cannot update the hidden page");
}

async function testConfigurationImportFileLifecycleGuards() {
    const oversizedHarness = makeHarness();
    const oversizedStatus = oversizedHarness.page.querySelector("#configurationPortabilityStatus");
    oversizedHarness.api.importConfigurationFile(oversizedHarness.page, {
        name: "oversized-config.json",
        size: oversizedHarness.api.maxConfigurationImportFileBytes + 1
    });
    assert.equal(oversizedHarness.readers.length, 0, "an oversized configuration import is rejected before FileReader starts");
    assert.equal(
        oversizedStatus.textContent,
        "The selected configuration file is too large. Choose a file no larger than 8 MiB.",
        "an oversized configuration import reports a bounded-file error"
    );

    const malformedHarness = makeHarness();
    const malformedPage = malformedHarness.page;
    const malformedApi = malformedHarness.api;
    let malformedPrepareCalls = 0;
    malformedApi.prepareConfigurationImport = () => { malformedPrepareCalls += 1; };
    malformedApi.importConfigurationFile(malformedPage, { name: "malformed-config.json" });
    assert.equal(malformedHarness.readers.length, 1, "a malformed configuration import still uses the cancellable reader");
    malformedHarness.readers[0].resolve(JSON.stringify({
        SchemaVersion: 1,
        Configuration: {},
        UserMappings: { unexpected: "object" }
    }));
    assert.equal(malformedPrepareCalls, 0, "a malformed collection cannot reach credential-field rendering");
    assert.equal(
        malformedPage.querySelector("#configurationPortabilityStatus").textContent,
        "The selected file is not a supported Hue configuration export.",
        "a malformed configuration import reports the supported-shape error"
    );

    const staleHarness = makeHarness();
    const { page, api, readers } = staleHarness;
    let prepareCalls = 0;
    api.prepareConfigurationImport = () => { prepareCalls += 1; };
    api.importConfigurationFile(page, { name: "stale-config.json" });
    assert.equal(readers.length, 1, "configuration import creates one file reader");
    const staleReader = readers[0];
    page.querySelector("#configurationPortabilityStatus").textContent = "unchanged after pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(staleReader.aborted, true, "pagehide aborts a pending configuration file reader");
    assert.equal(page._hueImportReader, null, "pagehide clears the pending configuration file reader");
    staleReader.resolve(JSON.stringify({
        SchemaVersion: 1,
        Configuration: { HueBridgeIp: "stale-bridge" }
    }));
    assert.equal(page._hueImportDocument, null, "a stale file read cannot repopulate the import document");
    assert.equal(
        page.querySelector("#configurationPortabilityStatus").textContent,
        "unchanged after pagehide",
        "a stale file read cannot update hidden-page status"
    );
    assert.equal(prepareCalls, 0, "a stale file read cannot prepare an import");

    const errorHarness = makeHarness();
    const errorPage = errorHarness.page;
    const errorApi = errorHarness.api;
    let errorPrepareCalls = 0;
    errorApi.prepareConfigurationImport = () => { errorPrepareCalls += 1; };
    errorApi.importConfigurationFile(errorPage, { name: "error-config.json" });
    assert.equal(errorHarness.readers.length, 1, "error-path import creates one file reader");
    const errorReader = errorHarness.readers[0];
    errorPage.querySelector("#configurationPortabilityStatus").textContent = "error sentinel after pagehide";
    errorApi.invalidatePageLifecycle(errorPage);
    errorReader.reject(new Error("stale file read failure"));
    assert.equal(
        errorPage.querySelector("#configurationPortabilityStatus").textContent,
        "error sentinel after pagehide",
        "a stale file-read error cannot update hidden-page status"
    );
    assert.equal(errorPrepareCalls, 0, "a stale file-read error cannot prepare an import");

    const activeHarness = makeHarness();
    const activePage = activeHarness.page;
    const activeApi = activeHarness.api;
    let preparedDocument;
    activeApi.prepareConfigurationImport = (_page, importDocument) => {
        preparedDocument = importDocument;
    };
    activeApi.importConfigurationFile(activePage, { name: "active-config.json" });
    assert.equal(activeHarness.readers.length, 1, "active import creates one file reader");
    activeHarness.readers[0].resolve(JSON.stringify({
        SchemaVersion: 1,
        Configuration: { HueBridgeIp: "active-bridge" }
    }));
    assert.equal(preparedDocument.SchemaVersion, 1, "a current file read preserves the export schema version");
    assert.equal(
        preparedDocument.Configuration.HueBridgeIp,
        "active-bridge",
        "a current file read prepares the selected import"
    );
    assert.equal(activePage._hueImportReader, null, "a completed file read clears its reader state");
}

async function testMappingDeviceRouteCredentialScope() {
    const harness = makeHarness();
    const { api } = harness;
    const userOne = "12345678-1234-1234-1234-1234567890ab";
    const userTwo = "87654321-4321-4321-4321-ba0987654321";
    const deviceId = "shared-playback-device";
    const mappingOne = "mapping-one";
    const mappingTwo = "mapping-two";
    const bridgeOne = "192.168.1.10.";
    const bridgeTwo = "192.168.1.11";

    const firstKey = api.getMappingDeviceRouteCredentialKey(deviceId, bridgeOne, userOne, mappingOne);
    const secondUserKey = api.getMappingDeviceRouteCredentialKey(deviceId, bridgeOne, userTwo, mappingTwo);
    const secondBridgeKey = api.getMappingDeviceRouteCredentialKey(deviceId, bridgeTwo, userOne, mappingOne);
    assert.notEqual(firstKey, secondUserKey, "route credential keys isolate mappings for different users");
    assert.notEqual(firstKey, secondBridgeKey, "route credential keys isolate bridge changes");

    api._hueDeviceRouteCredentials = {};
    api.rememberMappingDeviceRouteCredentials(deviceId, "app-one", "client-one", bridgeOne, userOne, mappingOne);
    api.rememberMappingDeviceRouteCredentials(deviceId, "app-two", "client-two", bridgeTwo, userOne, mappingOne);
    api.rememberMappingDeviceRouteCredentials(deviceId, "app-other", "client-other", bridgeOne, userTwo, mappingTwo);

    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, "192.168.1.10", userOne, mappingOne).appKey,
        "app-one",
        "matching route identity returns its own cached App Key"
    );
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, bridgeTwo, userOne, mappingOne).appKey,
        "app-two",
        "changed bridge returns only the replacement route key"
    );
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, bridgeOne, userTwo, mappingTwo).appKey,
        "app-other",
        "different mapping owner returns only its own cached App Key"
    );
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, "192.168.1.12", userOne, mappingOne).appKey,
        "",
        "unknown bridge cannot reuse another bridge's route credentials"
    );

    api.forgetMappingDeviceRouteCredentials(deviceId, bridgeOne, userOne, mappingOne);
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, bridgeOne, userOne, mappingOne).appKey,
        "",
        "removing a route clears only its scoped cached credentials"
    );
    assert.equal(
        api.getMappingDeviceRouteCredentials(deviceId, bridgeTwo, userOne, mappingOne).appKey,
        "app-two",
        "removing one route scope preserves a distinct bridge scope"
    );
}

async function testStoredDeviceRouteCredentialFlags() {
    const harness = makeHarness();
    const { page, api } = harness;
    const userId = "12345678-1234-1234-1234-1234567890ab";
    const deviceId = "living-room-tv";
    api.mappingEditingUserId = userId;
    api.mappingEditingMappingId = "mapping-one";
    page.querySelector("#mappingUserSelect").value = userId;
    page.querySelector("#mappingDeviceRouteSelect").value = deviceId;
    page.querySelector("#mappingDeviceTargets").value = JSON.stringify([{
        DeviceId: deviceId,
        HasAppKey: true,
        HasClientKey: true
    }]);

    const target = { userId, deviceId };
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, false), true, "a matching route with an App Key can use stored credentials");
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, true), true, "a matching route with both keys can satisfy client-key actions");

    page.querySelector("#mappingDeviceTargets").value = JSON.stringify([{
        DeviceId: deviceId,
        HasAppKey: true,
        HasClientKey: false
    }]);
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, false), true, "a route without a Client Key remains usable for REST-only actions");
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, true), false, "a route without a Client Key fails closed for DTLS actions");

    page.querySelector("#mappingDeviceTargets").value = JSON.stringify([{
        DeviceId: deviceId,
        HasAppKey: false,
        HasClientKey: true
    }]);
    assert.equal(api.canUseStoredDeviceRouteCredentials(target, false), false, "a route without an App Key fails closed");
}

async function testCredentialPreflightPagehideGuard() {
    const harness = makeHarness();
    const { page, api } = harness;
    let resolvePreflight;
    api.ensureBridgeCertificate = () => new Promise(resolve => { resolvePreflight = resolve; });
    let actionCalls = 0;

    const request = api.runWithBridgeCertificate(page, "192.168.1.50", null, () => {
        actionCalls += 1;
        return Promise.resolve("credential request started");
    }, "preview");
    api.invalidatePageLifecycle(page);
    resolvePreflight(true);

    await assert.rejects(request, error => error && error.huePageLifecycleStale === true, "stale preflight rejects with a lifecycle marker");
    assert.equal(actionCalls, 0, "a certificate approval completed after pagehide cannot start a credential request");
}

async function testBridgeCertificateTrustPromptPagehideGuard() {
    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    let confirm;
    dashboard.confirm = (_message, _title, callback) => { confirm = callback; };

    const generation = api.ensurePageLifecycle(page);
    const operation = api.ensureBridgeCertificate(
        page,
        "192.168.1.50",
        page.querySelector("#bridgeStatus"),
        generation);
    assert.equal(requests.length, 1, "certificate verification starts one probe request");
    assert.equal(requests[0].options.url, "HueSync/BridgeCertificate?ipAddress=192.168.1.50", "certificate verification scopes the probe to the bridge");

    requests[0].resolve({ fingerprint: "AA:BB", isPinned: false });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(typeof confirm, "function", "untrusted certificate asks for explicit approval");

    api.invalidatePageLifecycle(page);
    confirm(true);
    await assert.rejects(
        operation,
        error => error && error.huePageLifecycleStale === true,
        "approval of a hidden-page trust prompt rejects with a lifecycle marker");
    assert.equal(requests.length, 1, "approval of a stale trust prompt sends no trust mutation");
}

async function testBridgeCertificateTrustRequestLifecycle() {
    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    let confirm;
    dashboard.confirm = (_message, _title, callback) => { confirm = callback; };

    const generation = api.ensurePageLifecycle(page);
    const operation = api.ensureBridgeCertificate(
        page,
        "192.168.1.50",
        page.querySelector("#bridgeStatus"),
        generation);
    requests[0].resolve({ fingerprint: "AA:BB", isPinned: false });
    await new Promise(resolve => setImmediate(resolve));
    confirm(true);

    assert.equal(requests.length, 2, "confirmed trust starts one mutation request");
    assert.equal(requests[1].options.type, "POST", "certificate trust uses POST");
    assert.equal(requests[1].options.url, "HueSync/BridgeCertificate/Trust", "certificate trust uses the dedicated route");
    assert.deepEqual(
        JSON.parse(requests[1].options.data),
        { ipAddress: "192.168.1.50", fingerprint: "AA:BB", confirm: true },
        "certificate trust scopes the mutation to the approved fingerprint");
    assert.equal(
        page._huePageRequests.bridgeCertificateTrust.request,
        requests[1].promise,
        "certificate trust is registered in the page lifecycle slot");

    requests[1].resolve({ fingerprint: "AA:BB" });
    await operation;
    assert.equal(page._hueBridgeCertificatePins["192.168.1.50"], "AA:BB", "current trust completion caches the approved fingerprint");
    assert.equal(page.querySelector("#bridgeStatus").style.background, "#4caf50", "current trust completion updates status");

    const staleHarness = makeHarness();
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    let staleConfirm;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirm = callback; };
    const staleGeneration = staleApi.ensurePageLifecycle(stalePage);
    const staleOperation = staleApi.ensureBridgeCertificate(
        stalePage,
        "192.168.1.50",
        stalePage.querySelector("#bridgeStatus"),
        staleGeneration);
    staleHarness.requests[0].resolve({ fingerprint: "AA:BB", isPinned: false });
    await new Promise(resolve => setImmediate(resolve));
    staleConfirm(true);
    assert.equal(staleHarness.requests.length, 2, "stale lifecycle test starts a trust mutation request");
    stalePage.querySelector("#bridgeStatus").textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[1].promise.aborted, true, "pagehide aborts an in-flight certificate trust request");
    staleHarness.requests[1].resolve({ fingerprint: "AA:BB" });
    await staleOperation;
    assert.equal(stalePage._hueBridgeCertificatePins, undefined, "invalidated trust cannot mutate the hidden page's pin cache");
    assert.equal(
        stalePage.querySelector("#bridgeStatus").textContent,
        "unchanged after pagehide",
        "invalidated trust cannot write a stale status");
}

async function testCredentialPreflightCancelGuard() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    let resolvePreflight;
    api.ensureBridgeCertificate = () => new Promise(resolve => { resolvePreflight = resolve; });
    let actionCalls = 0;

    const request = api.runWithBridgeCertificate(page, "192.168.1.50", null, () => {
        actionCalls += 1;
        return Promise.resolve("credential request started");
    }, "preview");
    page._huePreviewRequest = request;
    api.cancelPreview(page);
    assert.equal(requests.length, 1, "canceling a preflight requests server-side diagnostic cleanup");
    requests[0].resolve({ canceled: false });
    resolvePreflight(true);

    await assert.rejects(request, error => error && error.huePageLifecycleStale === true, "canceled preflight rejects with a lifecycle marker");
    assert.equal(actionCalls, 0, "explicit diagnostic cancellation cannot start a credential request after approval");
}

async function testCredentialPreflightTargetMutationGuard() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    let resolvePreflight;
    api.ensureBridgeCertificate = () => new Promise(resolve => { resolvePreflight = resolve; });
    page.querySelector("#hueBridgeIp").value = "192.168.1.50";
    page.querySelector("#hueAppKey").value = "old-app-key";
    page.querySelector("#hueClientKey").value = "old-client-key";
    page.querySelector("#channelIds").value = "1, 2";

    api.testDefaultConnection(page);
    const request = page._huePreviewRequest;
    assert.ok(request && typeof request.then === "function", "connection test tracks the pending certificate preflight");
    page.querySelector("#hueBridgeIp").value = "192.168.1.51";
    resolvePreflight(true);

    await assert.rejects(request, error => error && error.huePageLifecycleStale === true, "edited credential target rejects a stale preflight");
    assert.equal(requests.length, 0, "editing the bridge while trust is pending cannot send the captured credentials");
}

async function testCredentialLifecyclePreflightPagehideGuard() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    let resolvePreflight;
    api.ensureBridgeCertificate = () => new Promise(resolve => { resolvePreflight = resolve; });

    const request = api.getCredentialLifecycleRequest(
        page,
        page._huePageGeneration,
        "credentialLifecycle",
        "192.168.1.50",
        null,
        "HueSync/EntertainmentAreas",
        { type: "POST", data: JSON.stringify({ ipAddress: "192.168.1.50", appKey: "secret" }) },
        { bridgeIp: "192.168.1.50", appKey: "secret" });
    api.invalidatePageLifecycle(page);
    resolvePreflight(true);

    await assert.rejects(request, error => error && error.huePageLifecycleStale === true, "stale lifecycle preflight rejects with a lifecycle marker");
    assert.equal(requests.length, 0, "a stale lifecycle preflight cannot start the credential-bearing API request");
}

async function testRegistrationLifecycleGuards() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    let resolvePreflight;
    api.ensureBridgeCertificate = () => new Promise(resolve => { resolvePreflight = resolve; });
    page.querySelector("#hueBridgeIp").value = "192.168.1.50";

    api.registerBridge(page);
    api.invalidatePageLifecycle(page);
    resolvePreflight(true);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(requests.length, 0, "a registration preflight completed after pagehide cannot start the link request");

    const mappingHarness = makeHarness();
    const mappingApi = mappingHarness.api;
    const mappingPage = mappingHarness.page;
    let resolveMappingPreflight;
    mappingApi.ensureBridgeCertificate = () => new Promise(resolve => { resolveMappingPreflight = resolve; });
    mappingHarness.page.querySelector("#mappingBridgeIp").value = "192.168.1.51";
    mappingApi.registerMappingBridge();
    mappingApi.invalidatePageLifecycle(mappingPage);
    resolveMappingPreflight(true);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(mappingHarness.requests.length, 0, "a mapping registration preflight completed after pagehide cannot start the link request");

    const targetHarness = makeHarness();
    const targetApi = targetHarness.api;
    const targetPage = targetHarness.page;
    let resolveTargetPreflight;
    targetApi.ensureBridgeCertificate = () => new Promise(resolve => { resolveTargetPreflight = resolve; });
    targetPage.querySelector("#hueBridgeIp").value = "192.168.1.52";
    targetApi.registerBridge(targetPage);
    targetPage.querySelector("#hueBridgeIp").value = "192.168.1.53";
    resolveTargetPreflight(true);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(targetHarness.requests.length, 0, "editing the global bridge while trust is pending cannot start registration");

    const mappingTargetHarness = makeHarness();
    const mappingTargetApi = mappingTargetHarness.api;
    const mappingTargetPage = mappingTargetHarness.page;
    let resolveMappingTargetPreflight;
    mappingTargetApi.ensureBridgeCertificate = () => new Promise(resolve => { resolveMappingTargetPreflight = resolve; });
    mappingTargetPage.querySelector("#mappingBridgeIp").value = "192.168.1.54";
    mappingTargetApi.registerMappingBridge();
    mappingTargetPage.querySelector("#mappingBridgeIp").value = "192.168.1.55";
    resolveMappingTargetPreflight(true);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(mappingTargetHarness.requests.length, 0, "editing the mapping bridge while trust is pending cannot start registration");
}

async function testMappingDeviceRouteChannelIsolation() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    // Keep this existing request-contract test focused on route payloads; the
    // certificate preflight has its own static contract checks.
    api.ensureBridgeCertificate = () => Promise.resolve(true);
    const userId = "12345678-1234-1234-1234-1234567890ab";
    const deviceId = "living-room-tv";

    api.mappingEditingUserId = userId;
    api.mappingEditingMappingId = "mapping-one";
    page.querySelector("#mappingUserSelect").value = userId;
    page.querySelector("#mappingDeviceRouteSelect").value = deviceId;
    page.querySelector("#mappingDeviceTargets").value = JSON.stringify([
        {
            DeviceId: deviceId,
            DeviceName: "Living room TV",
            HueBridgeIp: "192.168.1.50",
            HasAppKey: true,
            HasClientKey: true,
            EntertainmentAreaId: "route-area",
            ChannelIdsOverride: "7, 8"
        }
    ]);
    page.querySelector("#mappingDeviceRouteId").value = deviceId;
    page.querySelector("#mappingDeviceRouteBridge").value = "192.168.1.50";
    page.querySelector("#mappingDeviceRouteAppKey").value = "route-app-key";
    page.querySelector("#mappingDeviceRouteClientKey").value = "route-client-key";
    page.querySelector("#mappingDeviceRouteAreaId").value = "route-area";
    page.querySelector("#mappingDeviceRouteChannels").value = "7, 8";
    page.querySelector("#mappingChannelIdsOverride").value = "1, 2";
    page.querySelector("#channelIds").value = "3, 4";

    assert.equal(
        api.getEffectiveMappingChannelIds(page),
        "7, 8",
        "a selected route uses its route-specific channel profile"
    );

    const routeLoad = api.loadMappingDeviceRouteChannels();
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(requests.length, 1, "route channel loading starts one request");
    assert.equal(requests[0].options.url, "HueSync/EntertainmentChannels", "route channel loading uses the channel endpoint");
    const routePayload = JSON.parse(requests[0].options.data);
    assert.deepEqual(
        routePayload,
        {
            ipAddress: "192.168.1.50",
            appKey: "route-app-key",
            entertainmentAreaId: "route-area",
            userId,
            deviceId
        },
        "route channel loading sends the selected route identity"
    );
    requests[0].resolve([{ channelId: 9 }, { ChannelId: 4 }]);
    await routeLoad;
    assert.equal(page.querySelector("#mappingDeviceRouteChannels").value, "4, 9", "route loading writes only the route channel field");
    assert.equal(page.querySelector("#mappingChannelIdsOverride").value, "1, 2", "route loading does not mutate the outer user profile");

    api.testMappingConnection();
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(requests.length, 2, "mapping connection test starts one request");
    const connectionPayload = JSON.parse(requests[1].options.data);
    assert.equal(connectionPayload.appKey, "route-app-key", "mapping connection test sends staged route app key");
    assert.equal(connectionPayload.clientKey, "route-client-key", "mapping connection test sends staged route client key");
    assert.equal(connectionPayload.channelIds, "4, 9", "mapping connection test uses the selected route channels");
    assert.equal(connectionPayload.deviceId, deviceId, "mapping connection test keeps the selected route identity");
    const connectionRequest = page._huePreviewRequest;
    requests[1].resolve({ areaFound: true, channelProfileValid: true, streamTested: false });
    await connectionRequest;
    await new Promise(resolve => setTimeout(resolve, 0));

    page._huePreviewTargetMetadataReady = true;
    api.previewMappingColor(page);
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(requests.length, 3, "mapping preview starts one request");
    const previewPayload = JSON.parse(requests[2].options.data);
    assert.equal(previewPayload.appKey, "route-app-key", "mapping preview sends staged route app key");
    assert.equal(previewPayload.clientKey, "route-client-key", "mapping preview sends staged route client key");
    assert.equal(previewPayload.channelIds, "4, 9", "mapping preview uses the selected route channels");
    assert.equal(previewPayload.deviceId, deviceId, "mapping preview keeps the selected route identity");
    const previewRequest = page._huePreviewRequest;
    requests[2].resolve({ succeeded: true, message: "preview complete" });
    await previewRequest;
    await new Promise(resolve => setTimeout(resolve, 0));

    page.querySelector("#mappingDeviceRouteChannels").value = "";
    assert.equal(
        api.getEffectiveMappingChannelIds(page),
        "1, 2",
        "a blank staged route profile inherits the outer user profile"
    );

    page.querySelector("#mappingBridgeIp").value = "192.168.1.60";
    page.querySelector("#mappingAppKey").value = "outer-app-key";
    page.querySelector("#mappingAreaSelect").value = "outer-area";
    page.querySelector("#mappingDeviceRouteChannels").value = "4, 9";
    const outerLoad = api.loadMappingChannels(page);
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(requests.length, 4, "outer mapping channel loading starts a separate request");
    const outerPayload = JSON.parse(requests[3].options.data);
    assert.equal(outerPayload.ipAddress, "192.168.1.60", "outer channel loading ignores the selected route bridge");
    assert.equal(outerPayload.entertainmentAreaId, "outer-area", "outer channel loading ignores the selected route area");
    assert.equal(outerPayload.deviceId, "", "outer channel loading does not send a device route");
    requests[3].resolve([{ channelId: 2 }]);
    await outerLoad;
    assert.equal(page.querySelector("#mappingChannelIdsOverride").value, "2", "outer loading writes the outer user profile");
    assert.equal(page.querySelector("#mappingDeviceRouteChannels").value, "4, 9", "outer loading preserves the route profile");
}

async function testMappingDeviceDiscoveryLifecycleGuards() {
    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    const userId = "12345678-1234-4234-8234-1234567890ab";
    const userSelect = page.querySelector("#mappingUserSelect");
    const button = page.querySelector("#mappingDiscoverDevicesBtn");
    const status = page.querySelector("#mappingDeviceDiscoveryStatus");
    userSelect.value = userId;
    api.refreshMappingDeviceRoutes = () => {};
    let loadingShows = 0;
    let loadingHides = 0;
    dashboard.showLoadingMsg = () => { loadingShows += 1; };
    dashboard.hideLoadingMsg = () => { loadingHides += 1; };

    const staleOperation = api.discoverMappingDevices(page);
    assert.equal(requests.length, 1, "device discovery starts one tracked request");
    assert.equal(requests[0].options.url, "HueSync/PlaybackDevices?userId=" + userId, "device discovery scopes the request to the selected Jellyfin user");
    assert.equal(button.disabled, true, "device discovery disables its page-local button while pending");
    assert.equal(page._hueMappingPlaybackDevicesLoading, true, "device discovery records its loading state");
    assert.equal(loadingShows, 1, "device discovery shows the loading indicator once");
    status.textContent = "unchanged after pagehide";
    api._huePlaybackDevices = [{ UserId: userId, DeviceId: "private-device" }];

    api.invalidatePageLifecycle(page);
    assert.equal(requests[0].promise.aborted, true, "pagehide aborts an in-flight device discovery request");
    assert.equal(button.disabled, false, "pagehide restores the page-local device discovery button");
    assert.equal(page._hueMappingPlaybackDevicesLoading, false, "pagehide clears device discovery loading state");
    assert.equal(api._huePlaybackDevices.length, 0, "pagehide clears cached playback-device metadata");
    assert.equal(loadingHides, 1, "pagehide hides the loading indicator owned by device discovery");

    requests[0].resolve([{ UserId: userId, DeviceId: "stale-device" }]);
    await staleOperation;
    assert.equal(status.textContent, "unchanged after pagehide", "stale device discovery cannot overwrite the hidden page");
    assert.equal(api._huePlaybackDevices.length, 0, "stale device discovery cannot repopulate cached playback-device metadata");

    api.beginPageLifecycle(page);
    const currentOperation = api.discoverMappingDevices(page);
    assert.equal(requests.length, 2, "device discovery can be retried after pagehide");
    requests[1].resolve([{ UserId: userId, DeviceId: "current-device" }]);
    await currentOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(button.disabled, false, "current device discovery re-enables its page-local button");
    assert.equal(page._hueMappingPlaybackDevicesLoading, false, "current device discovery clears its loading state");
    assert.equal(api._huePlaybackDevices.length, 1, "current device discovery retains the current-user row");
    assert.equal(api._huePlaybackDevices[0].DeviceId, "current-device", "current device discovery retains the current device identity");
    assert.equal(loadingHides, 2, "current device discovery hides the loading indicator after completion");

    const changedTargetOperation = api.discoverMappingDevices(page);
    assert.equal(requests.length, 3, "a target-change scenario starts a fresh device discovery request");
    const changedUserId = "87654321-4321-4234-9234-ba0987654321";
    userSelect.value = changedUserId;
    status.textContent = "unchanged after user change";
    api._huePlaybackDevices = [];
    requests[2].resolve([{ UserId: userId, DeviceId: "stale-user-device" }]);
    await changedTargetOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(button.disabled, false, "a stale user-target response re-enables the page-local discovery button");
    assert.equal(page._hueMappingPlaybackDevicesLoading, false, "a stale user-target response clears discovery loading state");
    assert.equal(api._huePlaybackDevices.length, 0, "a stale user-target response cannot repopulate playback-device metadata");
    assert.equal(status.textContent, "unchanged after user change", "a stale user-target response cannot overwrite status");
    assert.equal(loadingHides, 3, "a stale user-target response releases its loading indicator");

    const retainedOwner = { _huePageActive: true };
    api._huePlaybackDevicesOwner = retainedOwner;
    api._huePlaybackDevices = [{ UserId: changedUserId, DeviceId: "active-page-device" }];
    const blockedOperation = api.discoverMappingDevices(page);
    assert.equal(blockedOperation, undefined, "a discovery blocked by another live page does not create an operation");
    assert.equal(requests.length, 3, "a discovery blocked by another live page sends no request");
    assert.equal(api._huePlaybackDevices.length, 1, "a discovery blocked by another live page preserves the active page cache");
    assert.equal(api._huePlaybackDevices[0].DeviceId, "active-page-device", "a discovery blocked by another live page preserves its device identity");
    assert.equal(button.disabled, false, "a discovery blocked by another live page leaves the button enabled");
    assert.equal(page._hueMappingPlaybackDevicesLoading, false, "a discovery blocked by another live page does not enter loading state");

    api.invalidatePageLifecycle(page);
    assert.equal(api._huePlaybackDevices.length, 1, "pagehide of a non-owning page cannot clear the active page cache");
    assert.equal(api._huePlaybackDevices[0].DeviceId, "active-page-device", "pagehide of a non-owning page preserves the active page device identity");

    const cancelHarness = makeHarness();
    const cancelPage = cancelHarness.page;
    const cancelApi = cancelHarness.api;
    const cancelUserSelect = cancelPage.querySelector("#mappingUserSelect");
    const cancelButton = cancelPage.querySelector("#mappingDiscoverDevicesBtn");
    cancelUserSelect.value = userId;
    cancelApi.refreshMappingDeviceRoutes = () => {};
    let cancelLoadingHides = 0;
    cancelHarness.dashboard.hideLoadingMsg = () => { cancelLoadingHides += 1; };
    cancelApi._huePlaybackDevices = [{ UserId: userId, DeviceId: "private-device" }];
    cancelApi.discoverMappingDevices(cancelPage);
    assert.equal(cancelHarness.requests.length, 1, "selection-clear cancellation starts one device discovery request");
    cancelUserSelect.value = "";
    cancelApi.discoverMappingDevices(cancelPage);
    assert.equal(cancelHarness.requests[0].promise.aborted, true, "clearing the selected user aborts the active discovery request");
    assert.equal(cancelPage._huePageRequests.mappingPlaybackDevices, undefined, "selection-clear cancellation removes the discovery request record");
    assert.equal(cancelPage._hueMappingPlaybackDevicesLoading, false, "selection-clear cancellation clears discovery loading state");
    assert.equal(cancelButton.disabled, false, "selection-clear cancellation restores the discovery button");
    assert.equal(cancelApi._huePlaybackDevices.length, 0, "selection-clear cancellation clears cached playback-device metadata");
    assert.equal(cancelLoadingHides, 1, "selection-clear cancellation hides the discovery loading indicator once");
    cancelHarness.requests[0].resolve([{ UserId: userId, DeviceId: "stale-device" }]);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(cancelApi._huePlaybackDevices.length, 0, "an aborted selection-clear request cannot repopulate playback-device metadata");
}

async function testBridgeDiscoveryLifecycleGuards() {
    for (const testCase of [
        {
            method: "discoverBridge",
            input: "#hueBridgeIp",
            button: "#discoverBtn",
            status: "#bridgeStatus",
            loading: "_hueBridgeDiscoveryLoading",
            key: "bridgeDiscovery",
            currentAddress: "192.168.1.20",
            staleAddress: "192.168.1.21"
        },
        {
            method: "discoverMappingBridge",
            input: "#mappingBridgeIp",
            button: "#mappingDiscoverBtn",
            status: "#mappingBridgeStatus",
            loading: "_hueMappingBridgeDiscoveryLoading",
            key: "mappingBridgeDiscovery",
            currentAddress: "192.168.1.30",
            staleAddress: "192.168.1.31"
        }
    ]) {
        const harness = makeHarness();
        const { page, api, requests, dashboard } = harness;
        const input = page.querySelector(testCase.input);
        const button = page.querySelector(testCase.button);
        const status = page.querySelector(testCase.status);
        let loadingShows = 0;
        let loadingHides = 0;
        dashboard.showLoadingMsg = () => { loadingShows += 1; };
        dashboard.hideLoadingMsg = () => { loadingHides += 1; };

        const staleOperation = api[testCase.method](page);
        assert.ok(staleOperation && typeof staleOperation.then === "function", `${testCase.method} returns a promise`);
        assert.equal(requests.length, 1, `${testCase.method} starts one tracked request`);
        assert.equal(requests[0].options.type, "GET", `${testCase.method} uses GET`);
        assert.equal(requests[0].options.url, "HueSync/DiscoverBridges", `${testCase.method} targets bridge discovery`);
        assert.equal(button.disabled, true, `${testCase.method} disables its page-local button while pending`);
        assert.equal(page[testCase.loading], true, `${testCase.method} records its loading state`);
        assert.equal(loadingShows, 1, `${testCase.method} shows the loading indicator once`);
        status.textContent = "unchanged after pagehide";

        api.invalidatePageLifecycle(page);
        assert.equal(requests[0].promise.aborted, true, `pagehide aborts ${testCase.method}`);
        assert.equal(button.disabled, false, `pagehide restores the ${testCase.method} button`);
        assert.equal(page[testCase.loading], false, `pagehide clears ${testCase.method} loading state`);
        assert.equal(loadingHides, 1, `pagehide hides the ${testCase.method} loading indicator`);
        requests[0].resolve({ IpAddresses: [testCase.staleAddress] });
        await staleOperation;
        assert.equal(input.value, "", `invalidated ${testCase.method} cannot overwrite the hidden page input`);
        assert.equal(status.textContent, "unchanged after pagehide", `invalidated ${testCase.method} cannot overwrite hidden-page status`);

        api.beginPageLifecycle(page);
        const currentOperation = api[testCase.method](page);
        assert.equal(requests.length, 2, `${testCase.method} can be retried after pagehide`);
        requests[1].resolve({ IpAddresses: [testCase.currentAddress] });
        await currentOperation;
        assert.equal(input.value, testCase.currentAddress, `current ${testCase.method} applies the discovered address`);
        assert.equal(button.disabled, false, `current ${testCase.method} re-enables its button`);
        assert.equal(page[testCase.loading], false, `current ${testCase.method} clears loading state`);
        assert.equal(loadingHides, 2, `current ${testCase.method} hides its loading indicator`);
        assert.equal(page._huePageRequests[testCase.key], undefined, `current ${testCase.method} removes settled request state`);

        input.value = "192.168.1.40";
        const changedTargetOperation = api[testCase.method](page);
        assert.equal(requests.length, 3, `${testCase.method} starts a fresh target-mutation request`);
        input.value = "192.168.1.41";
        status.textContent = "unchanged after address edit";
        requests[2].resolve({ IpAddresses: [testCase.staleAddress] });
        await changedTargetOperation;
        assert.equal(input.value, "192.168.1.41", `stale ${testCase.method} cannot overwrite an edited address`);
        assert.equal(status.textContent, "unchanged after address edit", `stale ${testCase.method} cannot overwrite edited-page status`);
        assert.equal(button.disabled, false, `stale ${testCase.method} re-enables its button`);
        assert.equal(page[testCase.loading], false, `stale ${testCase.method} clears loading state`);
        assert.equal(loadingHides, 3, `stale ${testCase.method} hides its loading indicator`);
        assert.equal(page._huePageRequests[testCase.key], undefined, `stale ${testCase.method} removes request state`);
    }
}

async function testConfigurationImportSubmitLifecycleGuards() {
    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    api.applyConfigurationImportCredentials = () => true;
    api.loadConfiguration = () => {};
    api.loadUserMappings = () => {};
    api.loadColorPresets = () => {};
    api.loadScenePlaylists = () => {};
    api.loadSceneSchedules = () => {};
    dashboard.confirm = (title, message, callback) => callback(true);
    page._hueImportDocument = { Configuration: { HueBridgeIp: "bridge.local" } };
    page._hueImportValidated = true;
    page._hueImportConfigurationVersion = "test-configuration-version";

    api.submitConfigurationImport(page);
    assert.equal(requests.length, 1, "configuration import starts one request after confirmation");
    assert.equal(requests[0].options.type, "POST", "configuration import uses POST");
    assert.equal(requests[0].options.url, "HueSync/Configuration/Import", "configuration import uses the import endpoint");
    assert.equal(JSON.parse(requests[0].options.data).expectedConfigurationVersion, "test-configuration-version", "configuration import sends the validated snapshot token");

    page.querySelector("#configurationPortabilityStatus").textContent = "unchanged after pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(requests[0].promise.aborted, true, "pagehide aborts configuration import UI request");
    requests[0].resolve({ message: "hidden stale import" });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(
        page.querySelector("#configurationPortabilityStatus").textContent,
        "unchanged after pagehide",
        "invalidated configuration import cannot update the hidden page");
    assert.equal(page._hueImportDocument, null, "pagehide clears the imported document before a later confirmation");
}

async function testConfigurationSaveSuppressesStaleConfigurationLoad() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    api.loadEntertainmentAreas = () => {};
    api.loadColorPresets = () => {};
    api.loadScenePlaylists = () => {};
    api.loadSceneSchedules = () => {};
    api.refreshStatus = (currentPage, message) => {
        currentPage.querySelector('#configurationPortabilityStatus').textContent = message;
    };

    const load = api.loadConfiguration(page);
    assert.equal(requests.length, 1, "configuration save race starts with one tracked load");
    page.querySelector('#hueBridgeIp').value = "edited-bridge";
    const save = api.saveConfiguration(page);
    assert.equal(requests.length, 2, "configuration save starts one replacement request");
    assert.equal(requests[0].promise.aborted, true, "configuration save aborts the stale configuration load");
    assert.equal(requests[1].options.type, "POST", "configuration save uses POST");
    assert.equal(requests[1].options.url, "HueSync/Configuration", "configuration save uses the configuration endpoint");
    assert.equal(
        Object.prototype.hasOwnProperty.call(JSON.parse(requests[1].options.data), "HueBridgeCertificatePins"),
        false,
        "configuration save cannot overwrite certificate pins from a stale page snapshot"
    );
    assert.equal(page.querySelector('#saveConfigurationBtn').disabled, true, "configuration save disables its button");

    requests[0].resolve({ HueBridgeIp: "old-bridge", SyncEnabled: true });
    await load;
    assert.equal(
        page.querySelector('#hueBridgeIp').value,
        "edited-bridge",
        "stale configuration load cannot overwrite edits made before save"
    );

    requests[1].resolve({ HueBridgeIp: "edited-bridge", HasAppKey: false, HasClientKey: false });
    await save;
    assert.equal(
        page.querySelector('#configurationPortabilityStatus').textContent,
        "Configuration saved.",
        "current configuration save reports success"
    );
    assert.equal(api.globalBridgeIp, "edited-bridge", "current configuration save updates the global target");
    assert.equal(page.querySelector('#saveConfigurationBtn').disabled, false, "configuration save re-enables its button");
}

async function testConfigurationSaveInvalidationSuppressesCallbacks() {
    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    api.refreshStatus = (currentPage, message) => {
        currentPage.querySelector('#configurationPortabilityStatus').textContent = message;
    };
    const save = api.saveConfiguration(page);
    assert.equal(requests.length, 1, "configuration save starts one tracked request");
    page.querySelector('#configurationPortabilityStatus').textContent = "sentinel before pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(requests[0].promise.aborted, true, "pagehide aborts configuration save");
    assert.equal(page._hueConfigurationSaving, false, "pagehide clears configuration save state");
    assert.equal(page.querySelector('#saveConfigurationBtn').disabled, false, "pagehide re-enables the save button");
    assert.equal(page._huePageRequests.configurationSave, undefined, "pagehide removes the save request record");
    requests[0].resolve({ HueBridgeIp: "stale-bridge" });
    await save;
    assert.equal(
        page.querySelector('#configurationPortabilityStatus').textContent,
        "sentinel before pagehide",
        "invalidated configuration save cannot update hidden-page status"
    );
    assert.equal(dashboard.alerts.length, 0, "invalidated configuration save cannot show a stale alert");
}

async function testConfigurationSaveDuplicateSubmitIsBounded() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    const statusMessages = [];
    api.refreshStatus = (_currentPage, message) => statusMessages.push(message);
    const first = api.saveConfiguration(page);
    const second = api.saveConfiguration(page);
    assert.equal(requests.length, 1, "duplicate configuration submit keeps one in-flight request");
    assert.ok(second && typeof second.then === "function", "duplicate configuration submit returns a settled no-op");
    assert.equal(page._hueConfigurationSaving, true, "configuration save remains marked busy while pending");
    requests[0].resolve({ HueBridgeIp: "saved-bridge", HasAppKey: false, HasClientKey: false });
    await Promise.all([first, second]);
    assert.deepEqual(statusMessages, ["Configuration saved."], "duplicate configuration submit reports one result");
    assert.equal(page._hueConfigurationSaving, false, "configuration save clears its busy state");
    assert.equal(page.querySelector('#saveConfigurationBtn').disabled, false, "duplicate configuration submit re-enables the button");
}

function configureColorPresetSaveHarness(harness) {
    const { page } = harness;
    page.querySelector("#previewPresetName").value = "Scene One";
    page.querySelector("#previewColor").value = "#123456";
    page.querySelector("#previewEffect").value = "Pulse";
    page.querySelector("#previewEffectSpeed").value = "125";
    page.querySelector("#previewBrightness").value = "80";
    page.querySelector("#previewDuration").value = "7";
    page.querySelector("#previewTransitionSeconds").value = "1";
    page.querySelector("#previewTransitionOutSeconds").value = "2";
    page.querySelector("#previewTransitionCurve").value = "EaseInOut";
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    return {
        button: page.querySelector("#savePreviewPresetBtn"),
        status: page.querySelector("#previewPresetStatus"),
        presetLoads: 0,
        playlistLoads: 0,
        scheduleLoads: 0,
        buttonUpdates: 0
    };
}

async function testColorPresetSaveLifecycleGuards() {
    const staleHarness = makeHarness();
    const staleState = configureColorPresetSaveHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadColorPresets = () => {
        staleState.presetLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadScenePlaylists = () => {
        staleState.playlistLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadSceneSchedules = () => {
        staleState.scheduleLoads += 1;
        return Promise.resolve();
    };
    staleApi.updatePresetButtons = () => {
        staleState.buttonUpdates += 1;
    };

    const staleSave = staleApi.saveColorPreset(stalePage);
    assert.ok(staleSave && typeof staleSave.then === "function", "color preset save returns a tracked promise");
    assert.equal(staleHarness.requests.length, 1, "color preset save starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "color preset save uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/ColorPresets", "color preset save targets the saved-scene endpoint");
    assert.deepEqual(JSON.parse(staleHarness.requests[0].options.data), {
        name: "Scene One",
        effect: "Pulse",
        effectSpeedPercent: 125,
        red: 18,
        green: 52,
        blue: 86,
        brightnessPercent: 80,
        durationSeconds: 7,
        transitionSeconds: 1,
        transitionOutSeconds: 2,
        transitionCurve: "EaseInOut"
    }, "color preset save sends the complete scene payload");
    assert.equal(stalePage._hueColorPresetSaving, true, "color preset save marks itself busy");
    assert.equal(staleState.button.disabled, true, "color preset save disables its button");
    assert.ok(stalePage._huePageRequests.colorPresetSave, "color preset save is tracked by the page lifecycle");

    staleState.status.textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts color preset save");
    assert.equal(stalePage._huePageRequests.colorPresetSave, undefined, "pagehide removes color preset save state");
    assert.equal(stalePage._hueColorPresetSaving, false, "pagehide clears color preset save state");
    assert.equal(staleState.button.disabled, false, "pagehide re-enables the color preset save button");
    staleHarness.requests[0].resolve({ name: "Stale Scene" });
    await staleSave;
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale color preset save cannot update hidden-page status");
    assert.equal(staleState.presetLoads, 0, "stale color preset save cannot reload saved scenes");
    assert.equal(staleState.playlistLoads, 0, "stale color preset save cannot reload playlists");
    assert.equal(staleState.scheduleLoads, 0, "stale color preset save cannot reload schedules");
    assert.equal(staleState.buttonUpdates, 0, "stale color preset save cannot update current-page controls");
    assert.equal(staleHarness.dashboard.alerts.length, 0, "stale color preset save cannot show an alert");

    const currentHarness = makeHarness();
    const currentState = configureColorPresetSaveHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadColorPresets = (_page, selectedName) => {
        currentState.presetLoads += 1;
        assert.equal(selectedName, "Scene One", "current color preset save reloads the saved scene");
        return Promise.resolve();
    };
    currentApi.loadScenePlaylists = () => {
        currentState.playlistLoads += 1;
        return Promise.resolve();
    };
    currentApi.loadSceneSchedules = (_page, selectedId) => {
        currentState.scheduleLoads += 1;
        assert.equal(selectedId, "cue-1", "current color preset save preserves the selected cue");
        return Promise.resolve();
    };
    currentApi.updatePresetButtons = () => {
        currentState.buttonUpdates += 1;
    };

    const currentSave = currentApi.saveColorPreset(currentPage);
    assert.equal(currentHarness.requests.length, 1, "current color preset save starts one request");
    currentHarness.requests[0].resolve({ name: "Scene One" });
    await currentSave;
    assert.equal(currentState.status.textContent, "Scene 'Scene One' saved.", "current color preset save reports success");
    assert.equal(currentState.presetLoads, 1, "current color preset save reloads saved scenes once");
    assert.equal(currentState.playlistLoads, 1, "current color preset save reloads playlists once");
    assert.equal(currentState.scheduleLoads, 1, "current color preset save reloads schedules once");
    assert.equal(currentPage._hueColorPresetSaving, false, "current color preset save clears busy state");
    assert.equal(currentState.button.disabled, false, "current color preset save re-enables its button");
    assert.equal(currentState.buttonUpdates, 1, "current color preset save refreshes current-page controls");

    const duplicateHarness = makeHarness();
    const duplicateState = configureColorPresetSaveHarness(duplicateHarness);
    const duplicatePage = duplicateHarness.page;
    const duplicateApi = duplicateHarness.api;
    duplicateApi.loadColorPresets = () => {
        duplicateState.presetLoads += 1;
        return Promise.resolve();
    };
    duplicateApi.loadScenePlaylists = () => {
        duplicateState.playlistLoads += 1;
        return Promise.resolve();
    };
    duplicateApi.loadSceneSchedules = () => {
        duplicateState.scheduleLoads += 1;
        return Promise.resolve();
    };
    duplicateApi.updatePresetButtons = () => {
        duplicateState.buttonUpdates += 1;
    };

    const first = duplicateApi.saveColorPreset(duplicatePage);
    const second = duplicateApi.saveColorPreset(duplicatePage);
    assert.equal(duplicateHarness.requests.length, 1, "duplicate color preset submit keeps one in-flight request");
    assert.ok(second && typeof second.then === "function", "duplicate color preset submit returns a settled no-op");
    assert.equal(duplicatePage._hueColorPresetSaving, true, "duplicate color preset submit remains marked busy");
    duplicateHarness.requests[0].resolve({ name: "Scene One" });
    await Promise.all([first, second]);
    assert.equal(duplicateState.presetLoads, 1, "duplicate color preset submit reloads saved scenes once");
    assert.equal(duplicateState.playlistLoads, 1, "duplicate color preset submit reloads playlists once");
    assert.equal(duplicateState.scheduleLoads, 1, "duplicate color preset submit reloads schedules once");
    assert.equal(duplicateState.buttonUpdates, 1, "duplicate color preset submit refreshes controls once");
    assert.equal(duplicatePage._hueColorPresetSaving, false, "duplicate color preset submit clears its busy state");
    assert.equal(duplicateState.button.disabled, false, "duplicate color preset submit re-enables its button");
}

function configureColorPresetDuplicateHarness(harness) {
    const { page } = harness;
    page.querySelector("#previewPresetSelect").value = "Scene One";
    return {
        button: page.querySelector("#duplicatePreviewPresetBtn"),
        status: page.querySelector("#previewPresetStatus"),
        presetLoads: 0,
        playlistLoads: 0,
        applyCalls: 0,
        buttonUpdates: 0
    };
}

async function testColorPresetDuplicateLifecycleGuards() {
    const staleHarness = makeHarness();
    const staleState = configureColorPresetDuplicateHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadColorPresets = () => {
        staleState.presetLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadScenePlaylists = () => {
        staleState.playlistLoads += 1;
        return Promise.resolve();
    };
    staleApi.applyColorPreset = () => {
        staleState.applyCalls += 1;
    };
    staleApi.updatePresetButtons = () => {
        staleState.buttonUpdates += 1;
    };

    const staleDuplicate = staleApi.duplicateColorPreset(stalePage);
    assert.ok(staleDuplicate && typeof staleDuplicate.then === "function", "color preset duplicate returns a tracked promise");
    assert.equal(staleHarness.requests.length, 1, "color preset duplicate starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "color preset duplicate uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/ColorPresets/Scene%20One/Duplicate", "color preset duplicate targets the selected saved scene");
    assert.equal(stalePage._hueColorPresetDuplicating, true, "color preset duplicate marks itself busy");
    assert.equal(staleState.button.disabled, true, "color preset duplicate disables its button");
    assert.ok(stalePage._huePageRequests.colorPresetDuplicate, "color preset duplicate is tracked by the page lifecycle");

    staleState.status.textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts color preset duplicate");
    assert.equal(stalePage._huePageRequests.colorPresetDuplicate, undefined, "pagehide removes color preset duplicate state");
    assert.equal(stalePage._hueColorPresetDuplicating, false, "pagehide clears color preset duplicate state");
    assert.equal(staleState.button.disabled, false, "pagehide re-enables the color preset duplicate button");
    staleHarness.requests[0].resolve({ name: "Stale Scene" });
    await staleDuplicate;
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale color preset duplicate cannot update hidden-page status");
    assert.equal(staleState.presetLoads, 0, "stale color preset duplicate cannot reload saved scenes");
    assert.equal(staleState.playlistLoads, 0, "stale color preset duplicate cannot reload playlists");
    assert.equal(staleState.applyCalls, 0, "stale color preset duplicate cannot apply the duplicate");
    assert.equal(staleState.buttonUpdates, 0, "stale color preset duplicate cannot update current-page controls");
    assert.equal(staleHarness.dashboard.alerts.length, 0, "stale color preset duplicate cannot show an alert");

    const currentHarness = makeHarness();
    const currentState = configureColorPresetDuplicateHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadColorPresets = (_page, selectedName) => {
        currentState.presetLoads += 1;
        assert.equal(selectedName, "Copied Scene", "current color preset duplicate reloads the returned scene");
        return Promise.resolve();
    };
    currentApi.loadScenePlaylists = () => {
        currentState.playlistLoads += 1;
        return Promise.resolve();
    };
    currentApi.applyColorPreset = () => {
        currentState.applyCalls += 1;
    };
    currentApi.updatePresetButtons = () => {
        currentState.buttonUpdates += 1;
    };

    const currentDuplicate = currentApi.duplicateColorPreset(currentPage);
    assert.equal(currentHarness.requests.length, 1, "current color preset duplicate starts one request");
    currentHarness.requests[0].resolve({ name: "Copied Scene" });
    await currentDuplicate;
    assert.equal(currentState.status.textContent, "Scene 'Scene One' duplicated as 'Copied Scene'.", "current color preset duplicate reports success");
    assert.equal(currentState.presetLoads, 1, "current color preset duplicate reloads saved scenes once");
    assert.equal(currentState.playlistLoads, 1, "current color preset duplicate reloads playlists once");
    assert.equal(currentState.applyCalls, 1, "current color preset duplicate applies the copied scene");
    assert.equal(currentPage._hueColorPresetDuplicating, false, "current color preset duplicate clears busy state");
    assert.equal(currentState.button.disabled, false, "current color preset duplicate re-enables its button");
    assert.equal(currentState.buttonUpdates, 1, "current color preset duplicate refreshes current-page controls");

    const duplicateHarness = makeHarness();
    const duplicateState = configureColorPresetDuplicateHarness(duplicateHarness);
    const duplicatePage = duplicateHarness.page;
    const duplicateApi = duplicateHarness.api;
    duplicateApi.loadColorPresets = () => {
        duplicateState.presetLoads += 1;
        return Promise.resolve();
    };
    duplicateApi.loadScenePlaylists = () => {
        duplicateState.playlistLoads += 1;
        return Promise.resolve();
    };
    duplicateApi.applyColorPreset = () => {
        duplicateState.applyCalls += 1;
    };
    duplicateApi.updatePresetButtons = () => {
        duplicateState.buttonUpdates += 1;
    };

    const first = duplicateApi.duplicateColorPreset(duplicatePage);
    const second = duplicateApi.duplicateColorPreset(duplicatePage);
    assert.equal(duplicateHarness.requests.length, 1, "duplicate color preset submit keeps one in-flight request");
    assert.ok(second && typeof second.then === "function", "duplicate color preset submit returns a settled no-op");
    assert.equal(duplicatePage._hueColorPresetDuplicating, true, "duplicate color preset submit remains marked busy");
    duplicateHarness.requests[0].resolve({ name: "Copied Scene" });
    await Promise.all([first, second]);
    assert.equal(duplicateState.presetLoads, 1, "duplicate color preset submit reloads saved scenes once");
    assert.equal(duplicateState.playlistLoads, 1, "duplicate color preset submit reloads playlists once");
    assert.equal(duplicateState.applyCalls, 1, "duplicate color preset submit applies the copy once");
    assert.equal(duplicateState.buttonUpdates, 1, "duplicate color preset submit refreshes controls once");
    assert.equal(duplicatePage._hueColorPresetDuplicating, false, "duplicate color preset submit clears its busy state");
    assert.equal(duplicateState.button.disabled, false, "duplicate color preset submit re-enables its button");
}

function configureColorPresetDeleteHarness(harness) {
    const { page } = harness;
    page.querySelector("#previewPresetSelect").value = "Scene One";
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    page.querySelector("#previewPresetName").value = "Scene One";
    return {
        button: page.querySelector("#deletePreviewPresetBtn"),
        status: page.querySelector("#previewPresetStatus"),
        name: page.querySelector("#previewPresetName")
    };
}

async function testColorPresetDeleteLifecycleGuards() {
    const confirmationHarness = makeHarness();
    const confirmationState = configureColorPresetDeleteHarness(confirmationHarness);
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    let confirmationCalls = 0;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
        confirmationCalls += 1;
        confirmation = callback;
    };
    const confirmationOperation = confirmationApi.deleteColorPreset(confirmationPage);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "color preset delete returns a promise");
    assert.equal(confirmationHarness.requests.length, 0, "color preset delete waits for confirmation before mutating configuration");
    assert.equal(confirmationCalls, 1, "color preset delete asks for one confirmation");
    assert.equal(confirmationState.button.disabled, true, "pending color preset delete disables its button");
    const duplicateConfirmation = confirmationApi.deleteColorPreset(confirmationPage);
    assert.equal(confirmationCalls, 1, "duplicate color preset delete does not open another confirmation");
    assert.equal(confirmationHarness.requests.length, 0, "duplicate color preset delete does not submit before confirmation");
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmationApi.beginPageLifecycle(confirmationPage);
    assert.equal(confirmationState.button.disabled, false, "pagehide restores the pending color preset delete button");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "stale color preset delete confirmation cannot start a request after pagehide");
    await confirmationOperation;
    await duplicateConfirmation;

    const staleHarness = makeHarness();
    const staleState = configureColorPresetDeleteHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    let stalePresetLoads = 0;
    let stalePlaylistLoads = 0;
    let staleScheduleLoads = 0;
    staleApi.loadColorPresets = () => {
        stalePresetLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadScenePlaylists = () => {
        stalePlaylistLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadSceneSchedules = () => {
        staleScheduleLoads += 1;
        return Promise.resolve();
    };
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.deleteColorPreset(stalePage);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed color preset delete starts one request");
    assert.equal(staleHarness.requests[0].options.type, "DELETE", "color preset delete uses DELETE");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/ColorPresets/Scene%20One", "color preset delete scopes the request to the selected scene");
    assert.equal(staleHarness.requests[0].options.dataType, "json", "color preset delete accepts the API response safely");
    assert.ok(stalePage._huePageRequests.colorPresetDelete, "color preset delete is tracked by the page lifecycle");
    assert.equal(stalePage._hueColorPresetDeleting, true, "color preset delete marks the page busy");
    assert.equal(staleState.button.disabled, true, "color preset delete keeps its button disabled while pending");
    staleState.status.textContent = "unchanged after pagehide";
    staleState.name.value = "current draft";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight color preset delete");
    assert.equal(stalePage._huePageRequests.colorPresetDelete, undefined, "pagehide removes the color preset delete request record");
    assert.equal(stalePage._hueColorPresetDeleting, false, "pagehide clears the color preset delete busy state");
    assert.equal(staleState.button.disabled, false, "pagehide restores the color preset delete button");
    staleHarness.requests[0].resolve({ message: "stale delete" });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale color preset delete cannot write hidden-page status");
    assert.equal(staleState.name.value, "current draft", "stale color preset delete cannot clear a reused page form");
    assert.equal(stalePresetLoads, 0, "stale color preset delete cannot reload saved scenes");
    assert.equal(stalePlaylistLoads, 0, "stale color preset delete cannot reload playlists");
    assert.equal(staleScheduleLoads, 0, "stale color preset delete cannot reload schedules");

    const currentHarness = makeHarness();
    const currentState = configureColorPresetDeleteHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    let currentPresetLoads = 0;
    let currentPlaylistLoads = 0;
    let currentScheduleLoads = 0;
    currentApi.loadColorPresets = () => {
        currentPresetLoads += 1;
        return Promise.resolve();
    };
    currentApi.loadScenePlaylists = () => {
        currentPlaylistLoads += 1;
        return Promise.resolve();
    };
    currentApi.loadSceneSchedules = (_page, selectedId) => {
        currentScheduleLoads += 1;
        assert.equal(selectedId, "cue-1", "current color preset delete preserves the selected cue");
        return Promise.resolve();
    };
    let currentConfirmation;
    let currentConfirmationCalls = 0;
    currentHarness.dashboard.confirm = (_message, _title, callback) => {
        currentConfirmationCalls += 1;
        currentConfirmation = callback;
    };
    const currentOperation = currentApi.deleteColorPreset(currentPage);
    currentConfirmation(true);
    assert.equal(currentHarness.requests.length, 1, "current color preset delete starts one request");
    const duplicateWhilePending = currentApi.deleteColorPreset(currentPage);
    await duplicateWhilePending;
    assert.equal(currentConfirmationCalls, 1, "pending color preset delete blocks duplicate confirmation");
    assert.equal(currentHarness.requests.length, 1, "pending color preset delete keeps one request in flight");
    currentHarness.requests[0].resolve({ message: "deleted" });
    await currentOperation;
    assert.equal(currentState.status.textContent, "Scene 'Scene One' deleted.", "current color preset delete reports success");
    assert.equal(currentState.name.value, "", "current color preset delete clears the scene form");
    assert.equal(currentPresetLoads, 1, "current color preset delete reloads saved scenes once");
    assert.equal(currentPlaylistLoads, 1, "current color preset delete reloads playlists once");
    assert.equal(currentScheduleLoads, 1, "current color preset delete reloads schedules once");
    assert.equal(currentPage._hueColorPresetDeleting, false, "current color preset delete clears the busy state");
    assert.equal(currentState.button.disabled, false, "current color preset delete restores its button");
    assert.equal(currentPage._huePageRequests.colorPresetDelete, undefined, "current color preset delete removes its settled lifecycle record");
}

function configureColorPresetBulkDeleteHarness(harness) {
    const { page } = harness;
    const bulkSelect = page.querySelector("#previewPresetBulkSelect");
    bulkSelect.options = [
        { value: "Scene One", selected: true },
        { value: "Scene Two", selected: true }
    ];
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    return {
        button: page.querySelector("#deleteSelectedPreviewPresetsBtn"),
        status: page.querySelector("#previewPresetBulkStatus"),
        bulkSelect,
        presetLoads: 0,
        playlistLoads: 0,
        scheduleLoads: 0
    };
}

async function testColorPresetBulkDeleteLifecycleGuards() {
    const confirmationHarness = makeHarness();
    const confirmationState = configureColorPresetBulkDeleteHarness(confirmationHarness);
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    let confirmationCalls = 0;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
        confirmationCalls += 1;
        confirmation = callback;
    };
    const confirmationOperation = confirmationApi.deleteColorPresetsBulk(confirmationPage);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "bulk color preset delete returns a promise");
    assert.equal(confirmationHarness.requests.length, 0, "bulk color preset delete waits for confirmation before mutating configuration");
    assert.equal(confirmationCalls, 1, "bulk color preset delete asks for one confirmation");
    assert.equal(confirmationState.button.disabled, true, "pending bulk color preset delete disables its button");
    for (const selector of [
        "#previewPresetBulkSelect",
        "#selectAllPreviewPresetsBtn",
        "#clearSelectedPreviewPresetsBtn",
        "#duplicateSelectedPreviewPresetsBtn",
        "#deleteSelectedPreviewPresetsBtn"
    ]) {
        assert.equal(confirmationPage.querySelector(selector).disabled, true, `pending bulk color preset delete disables ${selector}`);
    }
    const pendingConfirmation = confirmationPage._hueColorPresetBulkDeleteConfirmation;
    const duplicateConfirmation = confirmationApi.deleteColorPresetsBulk(confirmationPage);
    assert.equal(confirmationCalls, 1, "duplicate bulk color preset delete does not open another confirmation");
    assert.equal(confirmationHarness.requests.length, 0, "duplicate bulk color preset delete does not submit before confirmation");
    let confirmationSettled = false;
    confirmationOperation.then(() => { confirmationSettled = true; });
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmationApi.beginPageLifecycle(confirmationPage);
    assert.equal(pendingConfirmation.canceled, true, "pagehide cancels the pending bulk color preset delete confirmation");
    assert.equal(confirmationState.button.disabled, false, "pagehide restores the pending bulk color preset delete button");
    assert.equal(confirmationPage._hueColorPresetBulkMutation, null, "pagehide releases the pending bulk color preset delete lock");
    for (const selector of [
        "#previewPresetBulkSelect",
        "#selectAllPreviewPresetsBtn",
        "#clearSelectedPreviewPresetsBtn",
        "#duplicateSelectedPreviewPresetsBtn",
        "#deleteSelectedPreviewPresetsBtn"
    ]) {
        assert.equal(confirmationPage.querySelector(selector).disabled, false, `pagehide restores ${selector} after bulk color preset delete confirmation`);
    }
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(confirmationSettled, true, "pagehide settles a bulk color preset delete confirmation when its dialog callback never runs");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "stale bulk color preset delete confirmation cannot start a request after pagehide");
    await confirmationOperation;
    await duplicateConfirmation;

    const staleHarness = makeHarness();
    const staleState = configureColorPresetBulkDeleteHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadColorPresets = () => {
        staleState.presetLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadScenePlaylists = () => {
        staleState.playlistLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadSceneSchedules = () => {
        staleState.scheduleLoads += 1;
        return Promise.resolve();
    };
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.deleteColorPresetsBulk(stalePage);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed bulk color preset delete starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "bulk color preset delete uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/ColorPresets/BulkDelete", "bulk color preset delete uses the bulk route");
    assert.deepEqual(JSON.parse(staleHarness.requests[0].options.data), {
        presetNames: ["Scene One", "Scene Two"]
    }, "bulk color preset delete snapshots the selected scene names");
    assert.ok(stalePage._huePageRequests.colorPresetBulkDelete, "bulk color preset delete is tracked by the page lifecycle");
    assert.equal(stalePage._hueColorPresetBulkDeleting, true, "bulk color preset delete marks the page busy");
    assert.equal(staleState.button.disabled, true, "bulk color preset delete keeps its button disabled while pending");
    staleState.status.textContent = "unchanged after pagehide";
    staleState.bulkSelect.options[0].selected = false;
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight bulk color preset delete");
    assert.equal(stalePage._huePageRequests.colorPresetBulkDelete, undefined, "pagehide removes the bulk color preset delete request record");
    assert.equal(stalePage._hueColorPresetBulkDeleting, false, "pagehide clears the bulk color preset delete busy state");
    assert.equal(staleState.button.disabled, false, "pagehide restores the bulk color preset delete button");
    assert.equal(stalePage._hueColorPresetBulkMutation, null, "pagehide releases the in-flight bulk color preset delete lock");
    for (const selector of [
        "#previewPresetBulkSelect",
        "#selectAllPreviewPresetsBtn",
        "#clearSelectedPreviewPresetsBtn",
        "#duplicateSelectedPreviewPresetsBtn",
        "#deleteSelectedPreviewPresetsBtn"
    ]) {
        assert.equal(stalePage.querySelector(selector).disabled, false, `pagehide restores ${selector} after bulk color preset delete`);
    }
    staleHarness.requests[0].resolve({ deletedCount: 2 });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale bulk color preset delete cannot write hidden-page status");
    assert.equal(staleState.bulkSelect.options[0].selected, false, "stale bulk color preset delete cannot rewrite selection state");
    assert.equal(staleState.bulkSelect.options[1].selected, true, "stale bulk color preset delete preserves the reused page selection");
    assert.equal(staleState.presetLoads, 0, "stale bulk color preset delete cannot reload saved scenes");
    assert.equal(staleState.playlistLoads, 0, "stale bulk color preset delete cannot reload playlists");
    assert.equal(staleState.scheduleLoads, 0, "stale bulk color preset delete cannot reload schedules");

    const currentHarness = makeHarness();
    const currentState = configureColorPresetBulkDeleteHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadColorPresets = () => {
        currentState.presetLoads += 1;
        return Promise.resolve();
    };
    currentApi.loadScenePlaylists = () => {
        currentState.playlistLoads += 1;
        return Promise.resolve();
    };
    currentApi.loadSceneSchedules = (_page, selectedId) => {
        currentState.scheduleLoads += 1;
        assert.equal(selectedId, "cue-1", "current bulk color preset delete preserves the selected cue");
        return Promise.resolve();
    };
    let currentConfirmation;
    let currentConfirmationCalls = 0;
    currentHarness.dashboard.confirm = (_message, _title, callback) => {
        currentConfirmationCalls += 1;
        currentConfirmation = callback;
    };
    const currentOperation = currentApi.deleteColorPresetsBulk(currentPage);
    currentConfirmation(true);
    assert.equal(currentHarness.requests.length, 1, "current bulk color preset delete starts one request");
    const duplicateWhilePending = currentApi.deleteColorPresetsBulk(currentPage);
    await duplicateWhilePending;
    assert.equal(currentConfirmationCalls, 1, "pending bulk color preset delete blocks duplicate confirmation");
    assert.equal(currentHarness.requests.length, 1, "pending bulk color preset delete keeps one request in flight");
    assert.equal(currentPage._hueColorPresetBulkMutation.key, "colorPresetBulkDelete", "bulk color preset delete owns the shared mutation lock");
    const duplicateWhileDeletePending = currentApi.duplicateColorPresetsBulk(currentPage);
    await duplicateWhileDeletePending;
    assert.equal(currentConfirmationCalls, 1, "bulk color preset duplicate is suppressed while delete is pending");
    assert.equal(currentHarness.requests.length, 1, "bulk color preset duplicate cannot submit while delete is pending");
    for (const selector of [
        "#previewPresetBulkSelect",
        "#selectAllPreviewPresetsBtn",
        "#clearSelectedPreviewPresetsBtn",
        "#duplicateSelectedPreviewPresetsBtn",
        "#deleteSelectedPreviewPresetsBtn"
    ]) {
        assert.equal(currentPage.querySelector(selector).disabled, true, `pending bulk color preset delete keeps ${selector} disabled`);
    }
    currentApi.clearSelectedColorPresets(currentPage);
    currentApi.selectAllColorPresets(currentPage);
    assert.equal(currentState.bulkSelect.options[0].selected, true, "bulk color preset delete blocks clear-selection mutation");
    assert.equal(currentState.bulkSelect.options[1].selected, true, "bulk color preset delete blocks select-all mutation");
    currentHarness.requests[0].resolve({ deletedCount: 2 });
    await currentOperation;
    assert.equal(currentState.status.textContent, "Deleted 2 saved scene(s). Playlists and scheduled cues were refreshed.", "current bulk color preset delete reports success");
    assert.equal(currentState.bulkSelect.options[0].selected, false, "current bulk color preset delete clears the first selection");
    assert.equal(currentState.bulkSelect.options[1].selected, false, "current bulk color preset delete clears the second selection");
    assert.equal(currentState.presetLoads, 1, "current bulk color preset delete reloads saved scenes once");
    assert.equal(currentState.playlistLoads, 1, "current bulk color preset delete reloads playlists once");
    assert.equal(currentState.scheduleLoads, 1, "current bulk color preset delete reloads schedules once");
    assert.equal(currentPage._hueColorPresetBulkDeleting, false, "current bulk color preset delete clears the busy state");
    assert.equal(currentPage._hueColorPresetBulkMutation, null, "current bulk color preset delete releases the shared mutation lock");
    assert.equal(currentState.button.disabled, true, "current bulk color preset delete leaves its button disabled with no selection");
    assert.equal(currentPage.querySelector("#duplicateSelectedPreviewPresetsBtn").disabled, true, "current bulk color preset delete leaves duplicate disabled with no selection");
    assert.equal(currentPage._huePageRequests.colorPresetBulkDelete, undefined, "current bulk color preset delete removes its settled lifecycle record");
}

function configureColorPresetBulkDuplicateHarness(harness) {
    const state = configureColorPresetBulkDeleteHarness(harness);
    state.button = harness.page.querySelector("#duplicateSelectedPreviewPresetsBtn");
    return state;
}

async function testColorPresetBulkDuplicateLifecycleGuards() {
    const confirmationHarness = makeHarness();
    const confirmationState = configureColorPresetBulkDuplicateHarness(confirmationHarness);
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    let confirmationCalls = 0;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
        confirmationCalls += 1;
        confirmation = callback;
    };
    const confirmationOperation = confirmationApi.duplicateColorPresetsBulk(confirmationPage);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "bulk color preset duplicate returns a promise");
    assert.equal(confirmationHarness.requests.length, 0, "bulk color preset duplicate waits for confirmation before mutating configuration");
    assert.equal(confirmationCalls, 1, "bulk color preset duplicate asks for one confirmation");
    assert.equal(confirmationState.button.disabled, true, "pending bulk color preset duplicate disables its button");
    for (const selector of [
        "#previewPresetBulkSelect",
        "#selectAllPreviewPresetsBtn",
        "#clearSelectedPreviewPresetsBtn",
        "#duplicateSelectedPreviewPresetsBtn",
        "#deleteSelectedPreviewPresetsBtn"
    ]) {
        assert.equal(confirmationPage.querySelector(selector).disabled, true, `pending bulk color preset duplicate disables ${selector}`);
    }
    const pendingConfirmation = confirmationPage._hueColorPresetBulkDuplicateConfirmation;
    const duplicateConfirmation = confirmationApi.duplicateColorPresetsBulk(confirmationPage);
    assert.equal(confirmationCalls, 1, "duplicate bulk color preset duplicate does not open another confirmation");
    assert.equal(confirmationHarness.requests.length, 0, "duplicate bulk color preset duplicate does not submit before confirmation");
    let confirmationSettled = false;
    confirmationOperation.then(() => { confirmationSettled = true; });
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmationApi.beginPageLifecycle(confirmationPage);
    assert.equal(pendingConfirmation.canceled, true, "pagehide cancels the pending bulk color preset duplicate confirmation");
    assert.equal(confirmationState.button.disabled, false, "pagehide restores the pending bulk color preset duplicate button");
    assert.equal(confirmationPage._hueColorPresetBulkMutation, null, "pagehide releases the pending bulk color preset duplicate lock");
    for (const selector of [
        "#previewPresetBulkSelect",
        "#selectAllPreviewPresetsBtn",
        "#clearSelectedPreviewPresetsBtn",
        "#duplicateSelectedPreviewPresetsBtn",
        "#deleteSelectedPreviewPresetsBtn"
    ]) {
        assert.equal(confirmationPage.querySelector(selector).disabled, false, `pagehide restores ${selector} after bulk color preset duplicate confirmation`);
    }
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(confirmationSettled, true, "pagehide settles a bulk color preset duplicate confirmation when its dialog callback never runs");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "stale bulk color preset duplicate confirmation cannot start a request after pagehide");
    await confirmationOperation;
    await duplicateConfirmation;

    const staleHarness = makeHarness();
    const staleState = configureColorPresetBulkDuplicateHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadColorPresets = (_page, selectedName) => {
        staleState.presetLoads += 1;
        assert.equal(selectedName, "Scene One Copy", "bulk color preset duplicate would select the first returned copy");
        return Promise.resolve();
    };
    staleApi.loadScenePlaylists = () => {
        staleState.playlistLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadSceneSchedules = () => {
        staleState.scheduleLoads += 1;
        return Promise.resolve();
    };
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.duplicateColorPresetsBulk(stalePage);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed bulk color preset duplicate starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "bulk color preset duplicate uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/ColorPresets/BulkDuplicate", "bulk color preset duplicate uses the bulk route");
    assert.deepEqual(JSON.parse(staleHarness.requests[0].options.data), {
        presetNames: ["Scene One", "Scene Two"]
    }, "bulk color preset duplicate snapshots the selected scene names");
    assert.ok(stalePage._huePageRequests.colorPresetBulkDuplicate, "bulk color preset duplicate is tracked by the page lifecycle");
    assert.equal(stalePage._hueColorPresetBulkDuplicating, true, "bulk color preset duplicate marks the page busy");
    assert.equal(staleState.button.disabled, true, "bulk color preset duplicate keeps its button disabled while pending");
    staleState.status.textContent = "unchanged after pagehide";
    staleState.bulkSelect.options[0].selected = false;
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight bulk color preset duplicate");
    assert.equal(stalePage._huePageRequests.colorPresetBulkDuplicate, undefined, "pagehide removes the bulk color preset duplicate request record");
    assert.equal(stalePage._hueColorPresetBulkDuplicating, false, "pagehide clears the bulk color preset duplicate busy state");
    assert.equal(staleState.button.disabled, false, "pagehide restores the bulk color preset duplicate button");
    assert.equal(stalePage._hueColorPresetBulkMutation, null, "pagehide releases the in-flight bulk color preset duplicate lock");
    for (const selector of [
        "#previewPresetBulkSelect",
        "#selectAllPreviewPresetsBtn",
        "#clearSelectedPreviewPresetsBtn",
        "#duplicateSelectedPreviewPresetsBtn",
        "#deleteSelectedPreviewPresetsBtn"
    ]) {
        assert.equal(stalePage.querySelector(selector).disabled, false, `pagehide restores ${selector} after bulk color preset duplicate`);
    }
    staleHarness.requests[0].resolve({ presets: [{ name: "Scene One Copy" }] });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale bulk color preset duplicate cannot write hidden-page status");
    assert.equal(staleState.bulkSelect.options[0].selected, false, "stale bulk color preset duplicate cannot rewrite selection state");
    assert.equal(staleState.bulkSelect.options[1].selected, true, "stale bulk color preset duplicate preserves the reused page selection");
    assert.equal(staleState.presetLoads, 0, "stale bulk color preset duplicate cannot reload saved scenes");
    assert.equal(staleState.playlistLoads, 0, "stale bulk color preset duplicate cannot reload playlists");
    assert.equal(staleState.scheduleLoads, 0, "stale bulk color preset duplicate cannot reload schedules");

    const currentHarness = makeHarness();
    const currentState = configureColorPresetBulkDuplicateHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadColorPresets = (_page, selectedName) => {
        currentState.presetLoads += 1;
        assert.equal(selectedName, "Scene One Copy", "current bulk color preset duplicate selects the first returned copy");
        return Promise.resolve();
    };
    currentApi.loadScenePlaylists = () => {
        currentState.playlistLoads += 1;
        return Promise.resolve();
    };
    currentApi.loadSceneSchedules = (_page, selectedId) => {
        currentState.scheduleLoads += 1;
        assert.equal(selectedId, "cue-1", "current bulk color preset duplicate preserves the selected cue");
        return Promise.resolve();
    };
    let currentConfirmation;
    let currentConfirmationCalls = 0;
    currentHarness.dashboard.confirm = (_message, _title, callback) => {
        currentConfirmationCalls += 1;
        currentConfirmation = callback;
    };
    const currentOperation = currentApi.duplicateColorPresetsBulk(currentPage);
    currentConfirmation(true);
    assert.equal(currentHarness.requests.length, 1, "current bulk color preset duplicate starts one request");
    const duplicateWhilePending = currentApi.duplicateColorPresetsBulk(currentPage);
    await duplicateWhilePending;
    assert.equal(currentConfirmationCalls, 1, "pending bulk color preset duplicate blocks duplicate confirmation");
    assert.equal(currentHarness.requests.length, 1, "pending bulk color preset duplicate keeps one request in flight");
    assert.equal(currentPage._hueColorPresetBulkMutation.key, "colorPresetBulkDuplicate", "bulk color preset duplicate owns the shared mutation lock");
    const deleteWhileDuplicatePending = currentApi.deleteColorPresetsBulk(currentPage);
    await deleteWhileDuplicatePending;
    assert.equal(currentConfirmationCalls, 1, "bulk color preset delete is suppressed while duplicate is pending");
    assert.equal(currentHarness.requests.length, 1, "bulk color preset delete cannot submit while duplicate is pending");
    for (const selector of [
        "#previewPresetBulkSelect",
        "#selectAllPreviewPresetsBtn",
        "#clearSelectedPreviewPresetsBtn",
        "#duplicateSelectedPreviewPresetsBtn",
        "#deleteSelectedPreviewPresetsBtn"
    ]) {
        assert.equal(currentPage.querySelector(selector).disabled, true, `pending bulk color preset duplicate keeps ${selector} disabled`);
    }
    currentApi.clearSelectedColorPresets(currentPage);
    currentApi.selectAllColorPresets(currentPage);
    assert.equal(currentState.bulkSelect.options[0].selected, true, "bulk color preset duplicate blocks clear-selection mutation");
    assert.equal(currentState.bulkSelect.options[1].selected, true, "bulk color preset duplicate blocks select-all mutation");
    currentHarness.requests[0].resolve({
        message: "Created two independent saved-scene copies.",
        presets: [{ name: "Scene One Copy" }, { name: "Scene Two Copy" }]
    });
    await currentOperation;
    assert.equal(currentState.status.textContent, "Created two independent saved-scene copies.", "current bulk color preset duplicate reports success");
    assert.equal(currentState.bulkSelect.options[0].selected, false, "current bulk color preset duplicate clears the first selection");
    assert.equal(currentState.bulkSelect.options[1].selected, false, "current bulk color preset duplicate clears the second selection");
    assert.equal(currentState.presetLoads, 1, "current bulk color preset duplicate reloads saved scenes once");
    assert.equal(currentState.playlistLoads, 1, "current bulk color preset duplicate reloads playlists once");
    assert.equal(currentState.scheduleLoads, 1, "current bulk color preset duplicate reloads schedules once");
    assert.equal(currentPage._hueColorPresetBulkDuplicating, false, "current bulk color preset duplicate clears the busy state");
    assert.equal(currentPage._hueColorPresetBulkMutation, null, "current bulk color preset duplicate releases the shared mutation lock");
    assert.equal(currentState.button.disabled, true, "current bulk color preset duplicate leaves its button disabled with no selection");
    assert.equal(currentPage.querySelector("#deleteSelectedPreviewPresetsBtn").disabled, true, "current bulk color preset duplicate leaves delete disabled with no selection");
    assert.equal(currentPage._huePageRequests.colorPresetBulkDuplicate, undefined, "current bulk color preset duplicate removes its settled lifecycle record");
}

async function testColorPresetBulkErrorDetailsAndRetry() {
    const duplicateHarness = makeHarness();
    const duplicateState = configureColorPresetBulkDuplicateHarness(duplicateHarness);
    const duplicatePage = duplicateHarness.page;
    const duplicateApi = duplicateHarness.api;
    duplicateApi.loadColorPresets = () => Promise.resolve();
    duplicateApi.loadScenePlaylists = () => Promise.resolve();
    duplicateApi.loadSceneSchedules = () => Promise.resolve();
    let duplicateConfirmation;
    let duplicateConfirmationCalls = 0;
    duplicateHarness.dashboard.confirm = (_message, _title, callback) => {
        duplicateConfirmationCalls += 1;
        duplicateConfirmation = callback;
    };

    const duplicateFailure = duplicateApi.duplicateColorPresetsBulk(duplicatePage);
    duplicateConfirmation(true);
    assert.equal(duplicateHarness.requests.length, 1, "bulk duplicate error test starts one request");
    duplicateHarness.requests[0].reject({
        responseJSON: {
            message: "Only 0 saved-scene slot(s) remain; no copies were created.",
            availableCapacity: 0
        }
    });
    await duplicateFailure;
    assert.match(
        duplicateState.status.textContent,
        /Only 0 saved-scene slot\(s\) remain; no copies were created\. Available saved-scene slots: 0\./,
        "bulk duplicate renders the server capacity error details"
    );
    assert.equal(duplicatePage._hueColorPresetBulkMutation, null, "bulk duplicate releases its lock after a rejected request");
    assert.equal(duplicateState.bulkSelect.options[0].selected, true, "bulk duplicate keeps selection after a rejected request for retry");
    assert.equal(duplicateState.button.disabled, false, "bulk duplicate re-enables its button after a rejected request");

    const duplicateRetry = duplicateApi.duplicateColorPresetsBulk(duplicatePage);
    assert.equal(duplicateConfirmationCalls, 2, "bulk duplicate retry asks for a fresh confirmation");
    assert.equal(duplicateHarness.requests.length, 1, "bulk duplicate retry waits for confirmation");
    duplicateConfirmation(true);
    assert.equal(duplicateHarness.requests.length, 2, "bulk duplicate retry submits one new request");
    duplicateHarness.requests[1].resolve({
        message: "Created two independent saved-scene copies.",
        presets: [{ name: "Scene One Copy" }, { name: "Scene Two Copy" }]
    });
    await duplicateRetry;
    assert.equal(duplicateState.status.textContent, "Created two independent saved-scene copies.", "bulk duplicate retry succeeds after the displayed error");

    const deleteHarness = makeHarness();
    const deleteState = configureColorPresetBulkDeleteHarness(deleteHarness);
    const deletePage = deleteHarness.page;
    const deleteApi = deleteHarness.api;
    deleteApi.loadColorPresets = () => Promise.resolve();
    deleteApi.loadScenePlaylists = () => Promise.resolve();
    deleteApi.loadSceneSchedules = () => Promise.resolve();
    let deleteConfirmation;
    let deleteConfirmationCalls = 0;
    deleteHarness.dashboard.confirm = (_message, _title, callback) => {
        deleteConfirmationCalls += 1;
        deleteConfirmation = callback;
    };

    const deleteFailure = deleteApi.deleteColorPresetsBulk(deletePage);
    deleteConfirmation(true);
    assert.equal(deleteHarness.requests.length, 1, "bulk delete error test starts one request");
    deleteHarness.requests[0].reject({
        responseText: JSON.stringify({
            message: "One or more selected saved scenes are still referenced; update those references first.",
            blockedPresets: [{ name: "Scene One", playlistCount: 1, scheduledCueCount: 2 }]
        })
    });
    await deleteFailure;
    assert.match(
        deleteState.status.textContent,
        /One or more selected saved scenes are still referenced; update those references first\. Blocked saved scenes: Scene One \(1 playlist reference, 2 scheduled-cue references\)\./,
        "bulk delete renders the server dependency details"
    );
    assert.equal(deletePage._hueColorPresetBulkMutation, null, "bulk delete releases its lock after a rejected request");
    assert.equal(deleteState.bulkSelect.options[0].selected, true, "bulk delete keeps selection after a rejected request for retry");
    assert.equal(deleteState.button.disabled, false, "bulk delete re-enables its button after a rejected request");

    const deleteRetry = deleteApi.deleteColorPresetsBulk(deletePage);
    assert.equal(deleteConfirmationCalls, 2, "bulk delete retry asks for a fresh confirmation");
    assert.equal(deleteHarness.requests.length, 1, "bulk delete retry waits for confirmation");
    deleteConfirmation(true);
    assert.equal(deleteHarness.requests.length, 2, "bulk delete retry submits one new request");
    deleteHarness.requests[1].resolve({ deletedCount: 2 });
    await deleteRetry;
    assert.equal(deleteState.status.textContent, "Deleted 2 saved scene(s). Playlists and scheduled cues were refreshed.", "bulk delete retry succeeds after the displayed error");
}

function configureColorPresetRenameHarness(harness) {
    const { page } = harness;
    page.querySelector("#previewPresetSelect").value = "Scene One";
    page.querySelector("#previewPresetName").value = "Renamed Scene";
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    return {
        button: page.querySelector("#renamePreviewPresetBtn"),
        status: page.querySelector("#previewPresetStatus"),
        name: page.querySelector("#previewPresetName"),
        presetLoads: 0,
        playlistLoads: 0,
        scheduleLoads: 0,
        buttonUpdates: 0
    };
}

async function testColorPresetRenameLifecycleGuards() {
    const staleHarness = makeHarness();
    const staleState = configureColorPresetRenameHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadColorPresets = () => {
        staleState.presetLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadScenePlaylists = () => {
        staleState.playlistLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadSceneSchedules = () => {
        staleState.scheduleLoads += 1;
        return Promise.resolve();
    };
    staleApi.updatePresetButtons = () => {
        staleState.buttonUpdates += 1;
    };

    const staleRename = staleApi.renameColorPreset(stalePage);
    assert.ok(staleRename && typeof staleRename.then === "function", "color preset rename returns a tracked promise");
    assert.equal(staleHarness.requests.length, 1, "color preset rename starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "color preset rename uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/ColorPresets/Scene%20One/Rename", "color preset rename scopes the request to the selected scene");
    assert.deepEqual(JSON.parse(staleHarness.requests[0].options.data), { newName: "Renamed Scene" }, "color preset rename sends the requested new name");
    assert.equal(stalePage._hueColorPresetRenaming, true, "color preset rename marks the page busy");
    assert.equal(staleState.button.disabled, true, "color preset rename disables its button");
    assert.ok(stalePage._huePageRequests.colorPresetRename, "color preset rename is tracked by the page lifecycle");

    staleState.status.textContent = "unchanged after pagehide";
    staleState.name.value = "current draft";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight color preset rename");
    assert.equal(stalePage._huePageRequests.colorPresetRename, undefined, "pagehide removes the color preset rename request record");
    assert.equal(stalePage._hueColorPresetRenaming, false, "pagehide clears color preset rename state");
    assert.equal(staleState.button.disabled, false, "pagehide restores the color preset rename button");
    staleHarness.requests[0].resolve({ name: "Stale Scene" });
    await staleRename;
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale color preset rename cannot write hidden-page status");
    assert.equal(staleState.name.value, "current draft", "stale color preset rename cannot overwrite a reused page form");
    assert.equal(staleState.presetLoads, 0, "stale color preset rename cannot reload saved scenes");
    assert.equal(staleState.playlistLoads, 0, "stale color preset rename cannot reload playlists");
    assert.equal(staleState.scheduleLoads, 0, "stale color preset rename cannot reload schedules");
    assert.equal(staleState.buttonUpdates, 0, "stale color preset rename cannot update current-page controls");
    assert.equal(staleHarness.dashboard.alerts.length, 0, "stale color preset rename cannot show an alert");

    const currentHarness = makeHarness();
    const currentState = configureColorPresetRenameHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadColorPresets = (_page, selectedName) => {
        currentState.presetLoads += 1;
        assert.equal(selectedName, "Renamed Scene", "current color preset rename reloads the returned scene");
        return Promise.resolve();
    };
    currentApi.loadScenePlaylists = () => {
        currentState.playlistLoads += 1;
        return Promise.resolve();
    };
    currentApi.loadSceneSchedules = (_page, selectedId) => {
        currentState.scheduleLoads += 1;
        assert.equal(selectedId, "cue-1", "current color preset rename preserves the selected cue");
        return Promise.resolve();
    };
    currentApi.updatePresetButtons = () => {
        currentState.buttonUpdates += 1;
    };

    const currentRename = currentApi.renameColorPreset(currentPage);
    assert.equal(currentHarness.requests.length, 1, "current color preset rename starts one request");
    currentHarness.requests[0].resolve({ name: "Renamed Scene" });
    await currentRename;
    assert.equal(currentState.status.textContent, "Scene 'Scene One' renamed to 'Renamed Scene'; playlist and scheduled-cue references were migrated.", "current color preset rename reports success");
    assert.equal(currentState.name.value, "Renamed Scene", "current color preset rename updates the scene form");
    assert.equal(currentState.presetLoads, 1, "current color preset rename reloads saved scenes once");
    assert.equal(currentState.playlistLoads, 1, "current color preset rename reloads playlists once");
    assert.equal(currentState.scheduleLoads, 1, "current color preset rename reloads schedules once");
    assert.equal(currentPage._hueColorPresetRenaming, false, "current color preset rename clears the busy state");
    assert.equal(currentState.button.disabled, false, "current color preset rename re-enables its button");
    assert.equal(currentState.buttonUpdates, 1, "current color preset rename refreshes current-page controls");
    assert.equal(currentPage._huePageRequests.colorPresetRename, undefined, "current color preset rename removes its settled lifecycle record");

    const duplicateHarness = makeHarness();
    const duplicateState = configureColorPresetRenameHarness(duplicateHarness);
    const duplicatePage = duplicateHarness.page;
    const duplicateApi = duplicateHarness.api;
    duplicateApi.loadColorPresets = () => {
        duplicateState.presetLoads += 1;
        return Promise.resolve();
    };
    duplicateApi.loadScenePlaylists = () => {
        duplicateState.playlistLoads += 1;
        return Promise.resolve();
    };
    duplicateApi.loadSceneSchedules = () => {
        duplicateState.scheduleLoads += 1;
        return Promise.resolve();
    };
    duplicateApi.updatePresetButtons = () => {
        duplicateState.buttonUpdates += 1;
    };

    const first = duplicateApi.renameColorPreset(duplicatePage);
    const second = duplicateApi.renameColorPreset(duplicatePage);
    assert.equal(duplicateHarness.requests.length, 1, "duplicate color preset rename keeps one in-flight request");
    assert.ok(second && typeof second.then === "function", "duplicate color preset rename returns a settled no-op");
    assert.equal(duplicatePage._hueColorPresetRenaming, true, "duplicate color preset rename remains marked busy");
    duplicateHarness.requests[0].resolve({ name: "Renamed Scene" });
    await Promise.all([first, second]);
    assert.equal(duplicateState.presetLoads, 1, "duplicate color preset rename reloads saved scenes once");
    assert.equal(duplicateState.playlistLoads, 1, "duplicate color preset rename reloads playlists once");
    assert.equal(duplicateState.scheduleLoads, 1, "duplicate color preset rename reloads schedules once");
    assert.equal(duplicateState.buttonUpdates, 1, "duplicate color preset rename refreshes controls once");
    assert.equal(duplicatePage._hueColorPresetRenaming, false, "duplicate color preset rename clears the busy state");
    assert.equal(duplicateState.button.disabled, false, "duplicate color preset rename re-enables its button");
    assert.equal(duplicatePage._huePageRequests.colorPresetRename, undefined, "duplicate color preset rename removes its settled lifecycle record");
}

function configureColorPresetDependenciesHarness(harness) {
    const { page } = harness;
    const select = page.querySelector("#previewPresetSelect");
    select.options = [
        { value: "Scene One", textContent: "Scene One" },
        { value: "Scene Two", textContent: "Scene Two" }
    ];
    select.value = "Scene One";
    select.selectedIndex = 0;
    return {
        select,
        button: page.querySelector("#inspectPreviewPresetBtn"),
        status: page.querySelector("#previewPresetStatus")
    };
}

async function testColorPresetDependenciesLifecycleGuards() {
    const staleHarness = makeHarness();
    const staleState = configureColorPresetDependenciesHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    const staleOperation = staleApi.inspectColorPresetDependencies(stalePage);
    assert.ok(staleOperation && typeof staleOperation.then === "function", "color preset dependency inspection returns a tracked promise");
    assert.equal(staleHarness.requests.length, 1, "color preset dependency inspection starts one request");
    assert.equal(staleHarness.requests[0].options.type, "GET", "color preset dependency inspection uses GET");
    assert.equal(
        staleHarness.requests[0].options.url,
        "HueSync/ColorPresets/Scene%20One/Dependencies",
        "color preset dependency inspection scopes the request to the selected scene"
    );
    assert.ok(stalePage._huePageRequests.colorPresetDependencies, "color preset dependency inspection is tracked by page lifecycle");
    assert.equal(staleState.button.disabled, true, "color preset dependency inspection disables its button while pending");
    const duplicateOperation = staleApi.inspectColorPresetDependencies(stalePage);
    await duplicateOperation;
    assert.equal(staleHarness.requests.length, 1, "duplicate color preset dependency inspection is suppressed");
    staleState.select.value = "Scene Two";
    staleState.status.textContent = "current scene status";
    staleHarness.requests[0].resolve({
        name: "Scene One",
        canDelete: false,
        playlistCount: 1,
        scheduledCueCount: 1,
        playlists: [{ name: "Playlist One", referenceCount: 2 }],
        scheduledCues: [{ name: "Cue One", enabled: true, referenceType: "DirectScene" }]
    });
    await staleOperation;
    assert.equal(staleState.status.textContent, "current scene status", "selection-changed dependency response cannot overwrite current status");
    assert.equal(staleHarness.dashboard.alerts.length, 0, "selection-changed dependency response cannot show a stale alert");
    assert.equal(stalePage._hueColorPresetDependenciesRequest, null, "selection-changed dependency request clears its pointer");
    assert.equal(stalePage._huePageRequests.colorPresetDependencies, undefined, "selection-changed dependency request removes its lifecycle record");
    assert.equal(staleState.button.disabled, false, "selection-changed dependency request restores its button");

    const pagehideHarness = makeHarness();
    const pagehideState = configureColorPresetDependenciesHarness(pagehideHarness);
    const pagehidePage = pagehideHarness.page;
    const pagehideApi = pagehideHarness.api;
    const pagehideOperation = pagehideApi.inspectColorPresetDependencies(pagehidePage);
    assert.equal(pagehideHarness.requests.length, 1, "pagehide dependency scenario starts one request");
    pagehideState.status.textContent = "unchanged after pagehide";
    pagehideApi.invalidatePageLifecycle(pagehidePage);
    assert.equal(pagehideHarness.requests[0].promise.aborted, true, "pagehide aborts the color preset dependency request");
    assert.equal(pagehidePage._huePageRequests.colorPresetDependencies, undefined, "pagehide removes color preset dependency request state");
    assert.equal(pagehidePage._hueColorPresetDependenciesRequest, null, "pagehide clears the color preset dependency request pointer");
    assert.equal(pagehideState.button.disabled, false, "pagehide restores the color preset dependency inspection button");
    pagehideHarness.requests[0].resolve({
        name: "Scene One",
        canDelete: true,
        playlistCount: 0,
        scheduledCueCount: 0,
        playlists: [],
        scheduledCues: []
    });
    await pagehideOperation;
    assert.equal(pagehideState.status.textContent, "unchanged after pagehide", "pagehide dependency response cannot overwrite status");
    assert.equal(pagehideHarness.dashboard.alerts.length, 0, "pagehide dependency response cannot show an alert");

    const currentHarness = makeHarness();
    const currentState = configureColorPresetDependenciesHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    const currentOperation = currentApi.inspectColorPresetDependencies(currentPage);
    assert.equal(currentState.button.disabled, true, "current dependency inspection keeps its button disabled while pending");
    currentHarness.requests[0].resolve({
        name: "Scene One",
        canDelete: false,
        playlistCount: 1,
        scheduledCueCount: 1,
        playlists: [{ name: "Playlist One", referenceCount: 2 }],
        scheduledCues: [{ name: "Cue One", enabled: true, referenceType: "DirectScene" }]
    });
    await currentOperation;
    assert.equal(currentState.status.textContent, "This scene is referenced by 1 playlist(s) and 1 scheduled cue(s).", "current dependency inspection renders its result");
    assert.equal(currentHarness.dashboard.alerts.length, 1, "current dependency inspection shows one result alert");
    assert.match(currentHarness.dashboard.alerts[0], /Scene One[\s\S]*Playlist One[\s\S]*Cue One/, "current dependency inspection alert includes the returned references");
    assert.equal(currentPage._hueColorPresetDependenciesRequest, null, "current dependency inspection clears its request pointer");
    assert.equal(currentPage._huePageRequests.colorPresetDependencies, undefined, "current dependency inspection removes its lifecycle record");
    assert.equal(currentState.button.disabled, false, "current dependency inspection restores its button");
}

function configureScenePlaylistSaveHarness(harness) {
    const { page, api } = harness;
    api.getScenePlaylistTargetSelection = () => ({
        targetAllEnabledMappings: false,
        includeDefaultTarget: false,
        targetUserIds: [],
        targetUserId: ""
    });
    page._hueScenePlaylistId = "playlist-1";
    page._hueScenePlaylistItems = ["Scene One"];
    page.querySelector("#scenePlaylistName").value = "Playlist One";
    page.querySelector("#scenePlaylistRepeatCount").value = "1";
    page.querySelector("#scenePlaylistPlaybackOrder").value = "Sequential";
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    return {
        button: page.querySelector("#saveScenePlaylistBtn"),
        status: page.querySelector("#scenePlaylistStatus"),
        playlistLoads: 0,
        scheduleLoads: 0,
        buttonUpdates: 0
    };
}

async function testScenePlaylistSaveLifecycleGuards() {
    const staleHarness = makeHarness();
    const staleState = configureScenePlaylistSaveHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadScenePlaylists = () => {
        staleState.playlistLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadSceneSchedules = () => {
        staleState.scheduleLoads += 1;
        return Promise.resolve();
    };
    staleApi.updateScenePlaylistButtons = () => {
        staleState.buttonUpdates += 1;
    };

    const staleSave = staleApi.saveScenePlaylist(stalePage);
    assert.ok(staleSave && typeof staleSave.then === "function", "scene playlist save returns a tracked promise");
    assert.equal(staleHarness.requests.length, 1, "scene playlist save starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "scene playlist save uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/ScenePlaylists", "scene playlist save targets the playlist endpoint");
    assert.equal(stalePage._hueScenePlaylistSaving, true, "scene playlist save marks itself busy");
    assert.equal(staleState.button.disabled, true, "scene playlist save disables its button");
    assert.ok(stalePage._huePageRequests.scenePlaylistSave, "scene playlist save is tracked by the page lifecycle");

    staleState.status.textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts scene playlist save");
    assert.equal(stalePage._huePageRequests.scenePlaylistSave, undefined, "pagehide removes scene playlist save state");
    assert.equal(stalePage._hueScenePlaylistSaving, false, "pagehide clears scene playlist save state");
    assert.equal(stalePage._hueScenePlaylistMutation, null, "pagehide releases the scene playlist save mutation lock");
    assert.equal(staleState.button.disabled, false, "pagehide re-enables the scene playlist save button");
    staleHarness.requests[0].resolve({ name: "Stale Playlist" });
    await staleSave;
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale scene playlist save cannot update hidden-page status");
    assert.equal(staleState.playlistLoads, 0, "stale scene playlist save cannot reload playlists");
    assert.equal(staleState.scheduleLoads, 0, "stale scene playlist save cannot reload schedules");
    assert.equal(staleState.buttonUpdates, 0, "stale scene playlist save cannot update current-page controls");
    assert.equal(staleHarness.dashboard.alerts.length, 0, "stale scene playlist save cannot show an alert");

    const currentHarness = makeHarness();
    const currentState = configureScenePlaylistSaveHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadScenePlaylists = (_page, selectedName) => {
        currentState.playlistLoads += 1;
        assert.equal(selectedName, "Saved Playlist", "current scene playlist save reloads the returned playlist");
        currentPage.querySelector("#scenePlaylistSelect").value = selectedName;
        return Promise.resolve();
    };
    currentApi.loadSceneSchedules = (_page, selectedId) => {
        currentState.scheduleLoads += 1;
        assert.equal(selectedId, "cue-1", "current scene playlist save preserves the selected cue");
        return Promise.resolve();
    };
    currentApi.updateScenePlaylistButtons = () => {
        currentState.buttonUpdates += 1;
    };

    const currentSave = currentApi.saveScenePlaylist(currentPage);
    assert.equal(currentHarness.requests.length, 1, "current scene playlist save starts one request");
    currentHarness.requests[0].resolve({ name: "Saved Playlist" });
    await currentSave;
    assert.equal(currentState.status.textContent, "Playlist 'Saved Playlist' saved.", "current scene playlist save reports success");
    assert.equal(currentState.playlistLoads, 1, "current scene playlist save reloads playlists once");
    assert.equal(currentState.scheduleLoads, 1, "current scene playlist save reloads schedules once");
    assert.equal(currentPage._hueScenePlaylistSaving, false, "current scene playlist save clears busy state");
    assert.equal(currentState.button.disabled, false, "current scene playlist save re-enables its button");
    assert.equal(currentState.buttonUpdates, 1, "current scene playlist save refreshes current-page controls");
}

function configureScenePlaylistDeleteHarness(harness) {
    const { page } = harness;
    const select = page.querySelector("#scenePlaylistSelect");
    select.value = "Playlist One";
    select.selectedIndex = 0;
    select.options = [{ value: "Playlist One", textContent: "Playlist One" }];
    return {
        button: page.querySelector("#deleteScenePlaylistBtn"),
        status: page.querySelector("#scenePlaylistStatus"),
        clearCalls: 0,
        playlistLoads: 0,
        scheduleLoads: 0
    };
}

async function testScenePlaylistDeleteLifecycleGuards() {
    const confirmationHarness = makeHarness();
    const confirmationState = configureScenePlaylistDeleteHarness(confirmationHarness);
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    let confirmationCalls = 0;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
        confirmationCalls += 1;
        confirmation = callback;
    };
    const confirmationOperation = confirmationApi.deleteScenePlaylist(confirmationPage);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "scene playlist delete returns a promise");
    assert.equal(confirmationHarness.requests.length, 0, "scene playlist delete waits for confirmation before mutating configuration");
    assert.equal(confirmationCalls, 1, "scene playlist delete asks for one confirmation");
    assert.equal(confirmationState.button.disabled, true, "pending scene playlist delete disables its button");
    const pendingConfirmation = confirmationPage._hueScenePlaylistDeleteConfirmation;
    const duplicateConfirmation = confirmationApi.deleteScenePlaylist(confirmationPage);
    assert.equal(confirmationCalls, 1, "duplicate scene playlist delete does not open another confirmation");
    assert.equal(confirmationHarness.requests.length, 0, "duplicate scene playlist delete does not submit before confirmation");
    let confirmationSettled = false;
    confirmationOperation.then(() => { confirmationSettled = true; });
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmationApi.beginPageLifecycle(confirmationPage);
    assert.equal(pendingConfirmation.canceled, true, "pagehide cancels the pending scene playlist delete confirmation");
    assert.equal(confirmationState.button.disabled, false, "pagehide restores the pending scene playlist delete button");
    assert.equal(confirmationPage._hueScenePlaylistDeleteConfirmation, null, "pagehide clears the pending scene playlist delete confirmation");
    assert.equal(confirmationPage._hueScenePlaylistMutation, null, "pagehide releases the pending scene playlist delete mutation lock");
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(confirmationSettled, true, "pagehide settles the pending scene playlist delete confirmation");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "stale scene playlist delete confirmation cannot start a request after pagehide");
    await Promise.all([confirmationOperation, duplicateConfirmation]);

    const staleHarness = makeHarness();
    const staleState = configureScenePlaylistDeleteHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.clearScenePlaylist = () => {
        staleState.clearCalls += 1;
    };
    staleApi.loadScenePlaylists = () => {
        staleState.playlistLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadSceneSchedules = () => {
        staleState.scheduleLoads += 1;
        return Promise.resolve();
    };
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.deleteScenePlaylist(stalePage);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed scene playlist delete starts one request");
    assert.equal(staleHarness.requests[0].options.type, "DELETE", "scene playlist delete uses DELETE");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/ScenePlaylists/Playlist%20One", "scene playlist delete scopes the request to the selected playlist");
    assert.equal(staleHarness.requests[0].options.dataType, "json", "scene playlist delete accepts the API response safely");
    assert.ok(stalePage._huePageRequests.scenePlaylistDelete, "scene playlist delete is tracked by the page lifecycle");
    assert.equal(stalePage._hueScenePlaylistDeleting, true, "scene playlist delete marks the page busy");
    assert.equal(staleState.button.disabled, true, "scene playlist delete keeps its button disabled while pending");
    staleState.status.textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight scene playlist delete");
    assert.equal(stalePage._huePageRequests.scenePlaylistDelete, undefined, "pagehide removes scene playlist delete request state");
    assert.equal(stalePage._hueScenePlaylistDeleting, false, "pagehide clears scene playlist delete busy state");
    assert.equal(stalePage._hueScenePlaylistMutation, null, "pagehide releases the scene playlist delete mutation lock");
    assert.equal(staleState.button.disabled, false, "pagehide restores the scene playlist delete button");
    staleHarness.requests[0].resolve({ message: "stale delete" });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale scene playlist delete cannot write hidden-page status");
    assert.equal(staleState.clearCalls, 0, "stale scene playlist delete cannot clear a reused page form");
    assert.equal(staleState.playlistLoads, 0, "stale scene playlist delete cannot reload playlists");
    assert.equal(staleState.scheduleLoads, 0, "stale scene playlist delete cannot reload schedules");

    const currentHarness = makeHarness();
    const currentState = configureScenePlaylistDeleteHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.clearScenePlaylist = (_page, clearSelect) => {
        currentState.clearCalls += 1;
        assert.equal(clearSelect, true, "current scene playlist delete clears the selected playlist form");
        currentPage.querySelector("#scenePlaylistSelect").value = "";
    };
    currentApi.loadScenePlaylists = () => {
        currentState.playlistLoads += 1;
        currentPage.querySelector("#scenePlaylistSelect").value = "";
        return Promise.resolve();
    };
    currentApi.loadSceneSchedules = () => {
        currentState.scheduleLoads += 1;
        return Promise.resolve();
    };
    let currentConfirmation;
    let currentConfirmationCalls = 0;
    currentHarness.dashboard.confirm = (_message, _title, callback) => {
        currentConfirmationCalls += 1;
        currentConfirmation = callback;
    };
    const currentOperation = currentApi.deleteScenePlaylist(currentPage);
    currentConfirmation(true);
    assert.equal(currentHarness.requests.length, 1, "current scene playlist delete starts one request");
    const duplicateWhilePending = currentApi.deleteScenePlaylist(currentPage);
    await duplicateWhilePending;
    assert.equal(currentConfirmationCalls, 1, "pending scene playlist delete blocks duplicate confirmation");
    assert.equal(currentHarness.requests.length, 1, "pending scene playlist delete keeps one request in flight");
    currentHarness.requests[0].resolve({ message: "deleted" });
    await currentOperation;
    assert.equal(currentState.status.textContent, "Playlist 'Playlist One' deleted.", "current scene playlist delete reports success");
    assert.equal(currentState.clearCalls, 1, "current scene playlist delete clears the playlist form");
    assert.equal(currentState.playlistLoads, 1, "current scene playlist delete reloads playlists once");
    assert.equal(currentState.scheduleLoads, 1, "current scene playlist delete reloads schedules once");
    assert.equal(currentPage._hueScenePlaylistDeleting, false, "current scene playlist delete clears the busy state");
    assert.equal(currentState.button.disabled, true, "current scene playlist delete leaves its button disabled with no selection");
    assert.equal(currentPage._huePageRequests.scenePlaylistDelete, undefined, "current scene playlist delete removes its settled lifecycle record");
}

function configureScenePlaylistIndividualMutationHarness(harness, operation) {
    const { page } = harness;
    const select = page.querySelector("#scenePlaylistSelect");
    select.value = "Playlist One";
    select.selectedIndex = 0;
    select.options = [{ value: "Playlist One", textContent: "Playlist One" }];
    page.querySelector("#scenePlaylistName").value = operation === "rename" ? "Renamed Playlist" : "Playlist One";
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    return {
        button: page.querySelector(operation === "rename" ? "#renameScenePlaylistBtn" : "#duplicateScenePlaylistBtn"),
        status: page.querySelector("#scenePlaylistStatus"),
        name: page.querySelector("#scenePlaylistName"),
        playlistLoads: 0,
        scheduleLoads: 0,
        buttonUpdates: 0
    };
}

async function testScenePlaylistIndividualMutationLifecycleGuards() {
    const mutationCases = [
        {
            operation: "duplicate",
            method: "duplicateScenePlaylist",
            key: "scenePlaylistDuplicate",
            flag: "_hueScenePlaylistDuplicating",
            route: "HueSync/ScenePlaylists/Playlist%20One/Duplicate",
            response: { name: "Playlist Copy" },
            success: "Playlist 'Playlist One' duplicated as 'Playlist Copy'."
        },
        {
            operation: "rename",
            method: "renameScenePlaylist",
            key: "scenePlaylistRename",
            flag: "_hueScenePlaylistRenaming",
            route: "HueSync/ScenePlaylists/Playlist%20One/Rename",
            response: { name: "Renamed Playlist" },
            success: "Playlist 'Playlist One' renamed to 'Renamed Playlist'; scheduled-cue references were migrated."
        }
    ];

    for (const testCase of mutationCases) {
        const staleHarness = makeHarness();
        const staleState = configureScenePlaylistIndividualMutationHarness(staleHarness, testCase.operation);
        const stalePage = staleHarness.page;
        const staleApi = staleHarness.api;
        staleApi.loadScenePlaylists = () => {
            staleState.playlistLoads += 1;
            return Promise.resolve();
        };
        staleApi.loadSceneSchedules = () => {
            staleState.scheduleLoads += 1;
            return Promise.resolve();
        };
        staleApi.updateScenePlaylistButtons = () => {
            staleState.buttonUpdates += 1;
        };

        const staleOperation = staleApi[testCase.method](stalePage);
        assert.ok(staleOperation && typeof staleOperation.then === "function", `${testCase.operation} scene playlist mutation returns a tracked promise`);
        assert.equal(staleHarness.requests.length, 1, `${testCase.operation} scene playlist mutation starts one request`);
        assert.equal(staleHarness.requests[0].options.type, "POST", `${testCase.operation} scene playlist mutation uses POST`);
        assert.equal(staleHarness.requests[0].options.url, testCase.route, `${testCase.operation} scene playlist mutation targets the selected playlist`);
        if (testCase.operation === "rename") {
            assert.deepEqual(JSON.parse(staleHarness.requests[0].options.data), { newName: "Renamed Playlist" }, "scene playlist rename sends the requested new name");
        }
        assert.equal(stalePage[testCase.flag], true, `${testCase.operation} scene playlist mutation marks the page busy`);
        assert.equal(staleState.button.disabled, true, `${testCase.operation} scene playlist mutation disables its button`);
        assert.ok(stalePage._huePageRequests[testCase.key], `${testCase.operation} scene playlist mutation is tracked by the page lifecycle`);

        staleState.status.textContent = "unchanged after pagehide";
        staleState.name.value = "current draft";
        staleApi.invalidatePageLifecycle(stalePage);
        assert.equal(staleHarness.requests[0].promise.aborted, true, `pagehide aborts in-flight ${testCase.operation} scene playlist mutation`);
        assert.equal(stalePage._huePageRequests[testCase.key], undefined, `pagehide removes ${testCase.operation} scene playlist mutation state`);
        assert.equal(stalePage[testCase.flag], false, `pagehide clears ${testCase.operation} scene playlist mutation state`);
        assert.equal(stalePage._hueScenePlaylistMutation, null, `pagehide releases the ${testCase.operation} scene playlist mutation lock`);
        assert.equal(staleState.button.disabled, false, `pagehide restores the ${testCase.operation} scene playlist mutation button`);
        staleHarness.requests[0].resolve(testCase.response);
        await staleOperation;
        assert.equal(staleState.status.textContent, "unchanged after pagehide", `stale ${testCase.operation} scene playlist mutation cannot write hidden-page status`);
        assert.equal(staleState.name.value, "current draft", `stale ${testCase.operation} scene playlist mutation cannot overwrite a reused page form`);
        assert.equal(staleState.playlistLoads, 0, `stale ${testCase.operation} scene playlist mutation cannot reload playlists`);
        assert.equal(staleState.scheduleLoads, 0, `stale ${testCase.operation} scene playlist mutation cannot reload schedules`);
        assert.equal(staleState.buttonUpdates, 0, `stale ${testCase.operation} scene playlist mutation cannot update current-page controls`);

        const currentHarness = makeHarness();
        const currentState = configureScenePlaylistIndividualMutationHarness(currentHarness, testCase.operation);
        const currentPage = currentHarness.page;
        const currentApi = currentHarness.api;
        currentApi.loadScenePlaylists = (_page, selectedName) => {
            currentState.playlistLoads += 1;
            assert.equal(selectedName, testCase.operation === "rename" ? "Renamed Playlist" : "Playlist Copy", `current ${testCase.operation} scene playlist mutation reloads the returned playlist`);
            currentPage.querySelector("#scenePlaylistSelect").value = selectedName;
            return Promise.resolve();
        };
        currentApi.loadSceneSchedules = (_page, selectedId) => {
            currentState.scheduleLoads += 1;
            assert.equal(selectedId, "cue-1", "current scene playlist rename preserves the selected cue");
            return Promise.resolve();
        };
        currentApi.updateScenePlaylistButtons = () => {
            currentState.buttonUpdates += 1;
        };

        const currentOperation = currentApi[testCase.method](currentPage);
        assert.equal(currentHarness.requests.length, 1, `current ${testCase.operation} scene playlist mutation starts one request`);
        currentHarness.requests[0].resolve(testCase.response);
        await currentOperation;
        assert.equal(currentState.status.textContent, testCase.success, `current ${testCase.operation} scene playlist mutation reports success`);
        if (testCase.operation === "rename") {
            assert.equal(currentState.name.value, "Renamed Playlist", "current scene playlist rename updates the playlist form");
        }
        assert.equal(currentState.playlistLoads, 1, `current ${testCase.operation} scene playlist mutation reloads playlists once`);
        assert.equal(currentState.scheduleLoads, testCase.operation === "rename" ? 1 : 0, `current ${testCase.operation} scene playlist mutation reloads schedules as required`);
        assert.equal(currentPage[testCase.flag], false, `current ${testCase.operation} scene playlist mutation clears busy state`);
        assert.equal(currentState.button.disabled, false, `current ${testCase.operation} scene playlist mutation re-enables its button`);
        assert.equal(currentState.buttonUpdates, 1, `current ${testCase.operation} scene playlist mutation refreshes current-page controls`);
        assert.equal(currentPage._huePageRequests[testCase.key], undefined, `current ${testCase.operation} scene playlist mutation removes its settled lifecycle record`);

        const duplicateHarness = makeHarness();
        const duplicateState = configureScenePlaylistIndividualMutationHarness(duplicateHarness, testCase.operation);
        const duplicatePage = duplicateHarness.page;
        const duplicateApi = duplicateHarness.api;
        duplicateApi.loadScenePlaylists = () => {
            duplicateState.playlistLoads += 1;
            duplicatePage.querySelector("#scenePlaylistSelect").value = testCase.operation === "rename" ? "Renamed Playlist" : "Playlist Copy";
            return Promise.resolve();
        };
        duplicateApi.loadSceneSchedules = () => {
            duplicateState.scheduleLoads += 1;
            return Promise.resolve();
        };
        duplicateApi.updateScenePlaylistButtons = () => {
            duplicateState.buttonUpdates += 1;
        };

        const first = duplicateApi[testCase.method](duplicatePage);
        const second = duplicateApi[testCase.method](duplicatePage);
        assert.equal(duplicateHarness.requests.length, 1, `duplicate ${testCase.operation} scene playlist mutation keeps one in-flight request`);
        assert.ok(second && typeof second.then === "function", `duplicate ${testCase.operation} scene playlist mutation returns a settled no-op`);
        assert.equal(duplicatePage[testCase.flag], true, `duplicate ${testCase.operation} scene playlist mutation remains marked busy`);
        duplicateHarness.requests[0].resolve(testCase.response);
        await Promise.all([first, second]);
        assert.equal(duplicateState.playlistLoads, 1, `duplicate ${testCase.operation} scene playlist mutation reloads playlists once`);
        assert.equal(duplicateState.scheduleLoads, testCase.operation === "rename" ? 1 : 0, `duplicate ${testCase.operation} scene playlist mutation reloads schedules as required`);
        assert.equal(duplicateState.buttonUpdates, 1, `duplicate ${testCase.operation} scene playlist mutation refreshes controls once`);
        assert.equal(duplicatePage[testCase.flag], false, `duplicate ${testCase.operation} scene playlist mutation clears busy state`);
        assert.equal(duplicateState.button.disabled, false, `duplicate ${testCase.operation} scene playlist mutation re-enables its button`);
    }
}

function configureScenePlaylistDependenciesHarness(harness) {
    const { page } = harness;
    const select = page.querySelector("#scenePlaylistSelect");
    select.options = [
        { value: "Playlist One", textContent: "Playlist One" },
        { value: "Playlist Two", textContent: "Playlist Two" }
    ];
    select.value = "Playlist One";
    select.selectedIndex = 0;
    return {
        select,
        button: page.querySelector("#inspectScenePlaylistBtn"),
        status: page.querySelector("#scenePlaylistStatus")
    };
}

async function testScenePlaylistDependenciesLifecycleGuards() {
    const staleHarness = makeHarness();
    const staleState = configureScenePlaylistDependenciesHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    const staleOperation = staleApi.inspectScenePlaylistDependencies(stalePage);
    assert.ok(staleOperation && typeof staleOperation.then === "function", "scene playlist dependency inspection returns a tracked promise");
    assert.equal(staleHarness.requests.length, 1, "scene playlist dependency inspection starts one request");
    assert.equal(staleHarness.requests[0].options.type, "GET", "scene playlist dependency inspection uses GET");
    assert.equal(
        staleHarness.requests[0].options.url,
        "HueSync/ScenePlaylists/Playlist%20One/Dependencies",
        "scene playlist dependency inspection scopes the request to the selected playlist"
    );
    assert.ok(stalePage._huePageRequests.scenePlaylistDependencies, "scene playlist dependency inspection is tracked by page lifecycle");
    assert.equal(staleState.button.disabled, true, "scene playlist dependency inspection disables its button while pending");
    const duplicateOperation = staleApi.inspectScenePlaylistDependencies(stalePage);
    await duplicateOperation;
    assert.equal(staleHarness.requests.length, 1, "duplicate scene playlist dependency inspection is suppressed");
    staleState.select.value = "Playlist Two";
    staleState.status.textContent = "current playlist status";
    staleHarness.requests[0].resolve({
        name: "Playlist One",
        canDelete: false,
        scheduledCueCount: 1,
        scheduledCues: [{ name: "Cue One", enabled: true }]
    });
    await staleOperation;
    assert.equal(staleState.status.textContent, "current playlist status", "selection-changed dependency response cannot overwrite current status");
    assert.equal(staleHarness.dashboard.alerts.length, 0, "selection-changed dependency response cannot show a stale alert");
    assert.equal(stalePage._hueScenePlaylistDependenciesRequest, null, "selection-changed dependency request clears its pointer");
    assert.equal(stalePage._huePageRequests.scenePlaylistDependencies, undefined, "selection-changed dependency request removes its lifecycle record");
    assert.equal(staleState.button.disabled, false, "selection-changed dependency request restores its button");

    const pagehideHarness = makeHarness();
    const pagehideState = configureScenePlaylistDependenciesHarness(pagehideHarness);
    const pagehidePage = pagehideHarness.page;
    const pagehideApi = pagehideHarness.api;
    const pagehideOperation = pagehideApi.inspectScenePlaylistDependencies(pagehidePage);
    assert.equal(pagehideHarness.requests.length, 1, "pagehide dependency scenario starts one request");
    pagehideState.status.textContent = "unchanged after pagehide";
    pagehideApi.invalidatePageLifecycle(pagehidePage);
    assert.equal(pagehideHarness.requests[0].promise.aborted, true, "pagehide aborts the dependency request");
    assert.equal(pagehidePage._huePageRequests.scenePlaylistDependencies, undefined, "pagehide removes dependency request state");
    assert.equal(pagehidePage._hueScenePlaylistDependenciesRequest, null, "pagehide clears the dependency request pointer");
    assert.equal(pagehideState.button.disabled, false, "pagehide restores the dependency inspection button");
    pagehideHarness.requests[0].resolve({
        name: "Playlist One",
        canDelete: true,
        scheduledCueCount: 0,
        scheduledCues: []
    });
    await pagehideOperation;
    assert.equal(pagehideState.status.textContent, "unchanged after pagehide", "pagehide dependency response cannot overwrite status");
    assert.equal(pagehideHarness.dashboard.alerts.length, 0, "pagehide dependency response cannot show an alert");

    const currentHarness = makeHarness();
    const currentState = configureScenePlaylistDependenciesHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    const currentOperation = currentApi.inspectScenePlaylistDependencies(currentPage);
    assert.equal(currentState.button.disabled, true, "current dependency inspection keeps its button disabled while pending");
    currentHarness.requests[0].resolve({
        name: "Playlist One",
        canDelete: false,
        scheduledCueCount: 1,
        scheduledCues: [{ name: "Cue One", enabled: true }]
    });
    await currentOperation;
    assert.equal(currentState.status.textContent, "This playlist is referenced by 1 scheduled cue(s).", "current dependency inspection renders its result");
    assert.equal(currentHarness.dashboard.alerts.length, 1, "current dependency inspection shows one result alert");
    assert.match(currentHarness.dashboard.alerts[0], /Playlist One[\s\S]*Cue One/, "current dependency inspection alert includes the returned references");
    assert.equal(currentPage._hueScenePlaylistDependenciesRequest, null, "current dependency inspection clears its request pointer");
    assert.equal(currentPage._huePageRequests.scenePlaylistDependencies, undefined, "current dependency inspection removes its lifecycle record");
    assert.equal(currentState.button.disabled, false, "current dependency inspection restores its button");
}

function scenePlaylistMutationControlSelectors() {
    return [
        "#scenePlaylistSelect",
        "#saveScenePlaylistBtn",
        "#duplicateScenePlaylistBtn",
        "#renameScenePlaylistBtn",
        "#deleteScenePlaylistBtn",
        "#scenePlaylistBulkSelect",
        "#selectAllScenePlaylistsBtn",
        "#clearSelectedScenePlaylistsBtn",
        "#duplicateSelectedScenePlaylistsBtn",
        "#deleteSelectedScenePlaylistsBtn"
    ];
}

function scenePlaylistDirectMutationControlSelectors() {
    return [
        "#scenePlaylistSelect",
        "#saveScenePlaylistBtn",
        "#duplicateScenePlaylistBtn",
        "#renameScenePlaylistBtn",
        "#deleteScenePlaylistBtn"
    ];
}

function configureScenePlaylistMutationHarness(harness, operation) {
    const { page, api } = harness;
    api.getScenePlaylistTargetSelection = () => ({
        targetAllEnabledMappings: false,
        includeDefaultTarget: false,
        targetUserIds: [],
        targetUserId: ""
    });
    const select = page.querySelector("#scenePlaylistSelect");
    select.value = "Playlist One";
    select.selectedIndex = 0;
    select.options = [
        { value: "Playlist One", textContent: "Playlist One" },
        { value: "Playlist Two", textContent: "Playlist Two" }
    ];
    page._hueScenePlaylistId = "playlist-1";
    page._hueScenePlaylistItems = ["Scene One"];
    page.querySelector("#scenePlaylistName").value = "Renamed Playlist";
    page.querySelector("#scenePlaylistRepeatCount").value = "1";
    page.querySelector("#scenePlaylistPlaybackOrder").value = "Sequential";
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    return {
        operation,
        buttonUpdates: 0,
        clearCalls: 0,
        playlistLoads: 0,
        scheduleLoads: 0,
        status: page.querySelector("#scenePlaylistStatus"),
        name: page.querySelector("#scenePlaylistName"),
        select
    };
}

function configureScenePlaylistMutationReloads(harness, state) {
    const { api } = harness;
    api.loadScenePlaylists = (_page, selectedName) => {
        state.playlistLoads += 1;
        _page.querySelector("#scenePlaylistSelect").value = selectedName || "";
        return Promise.resolve();
    };
    api.loadSceneSchedules = () => {
        state.scheduleLoads += 1;
        return Promise.resolve();
    };
    api.updateScenePlaylistButtons = () => {
        state.buttonUpdates += 1;
    };
}

async function testScenePlaylistMutationLockGuards() {
    const cases = [
        {
            owner: "save",
            method: "saveScenePlaylist",
            opposite: "duplicateScenePlaylist",
            response: { name: "Renamed Playlist" },
            flag: "_hueScenePlaylistSaving"
        },
        {
            owner: "duplicate",
            method: "duplicateScenePlaylist",
            opposite: "renameScenePlaylist",
            response: { name: "Playlist Copy" },
            flag: "_hueScenePlaylistDuplicating"
        },
        {
            owner: "rename",
            method: "renameScenePlaylist",
            opposite: "duplicateScenePlaylist",
            response: { name: "Renamed Playlist" },
            flag: "_hueScenePlaylistRenaming"
        },
        {
            owner: "delete",
            method: "deleteScenePlaylist",
            opposite: "duplicateScenePlaylist",
            response: null,
            flag: "_hueScenePlaylistDeleting",
            confirmation: true
        }
    ];

    for (const testCase of cases) {
        const harness = makeHarness();
        const state = configureScenePlaylistMutationHarness(harness, testCase.owner);
        const { page, api, requests, dashboard } = harness;
        configureScenePlaylistMutationReloads(harness, state);
        let confirmation;
        let confirmationCalls = 0;
        dashboard.confirm = (_message, _title, callback) => {
            confirmationCalls += 1;
            confirmation = callback;
        };

        const ownerOperation = api[testCase.method](page);
        assert.ok(ownerOperation && typeof ownerOperation.then === "function", `${testCase.owner} scene playlist mutation returns a promise`);
        assert.equal(page._hueScenePlaylistMutation.key, `scenePlaylist${testCase.owner[0].toUpperCase()}${testCase.owner.slice(1)}`, `${testCase.owner} scene playlist mutation owns the direct lock`);
        for (const selector of scenePlaylistMutationControlSelectors()) {
            assert.equal(page.querySelector(selector).disabled, true, `${testCase.owner} scene playlist mutation disables ${selector}`);
        }

        const requestCount = requests.length;
        const oppositeOperation = api[testCase.opposite](page);
        await oppositeOperation;
        assert.equal(requests.length, requestCount, `${testCase.owner} scene playlist mutation suppresses opposite request`);
        assert.equal(confirmationCalls, testCase.confirmation ? 1 : 0, `${testCase.owner} scene playlist mutation suppresses opposite confirmation`);
        assert.equal(!!page[testCase.flag], testCase.confirmation ? false : true, `${testCase.owner} scene playlist mutation retains its ownership state`);
        for (const selector of scenePlaylistMutationControlSelectors()) {
            assert.equal(page.querySelector(selector).disabled, true, `${testCase.owner} scene playlist mutation keeps ${selector} disabled during arbitration`);
        }

        if (testCase.confirmation) {
            confirmation(false);
        } else {
            assert.equal(requests.length, 1, `${testCase.owner} scene playlist mutation starts one request`);
            requests[0].resolve(testCase.response);
        }
        await ownerOperation;
        assert.equal(page._hueScenePlaylistMutation, null, `${testCase.owner} scene playlist mutation releases the direct lock`);
        assert.equal(!!page[testCase.flag], false, `${testCase.owner} scene playlist mutation clears its ownership state`);
        for (const selector of scenePlaylistDirectMutationControlSelectors()) {
            assert.equal(page.querySelector(selector).disabled, false, `${testCase.owner} scene playlist mutation restores ${selector}`);
        }
    }
}

async function testScenePlaylistMutationSelectionGuards() {
    const cases = [
        {
            method: "duplicateScenePlaylist",
            response: { name: "Playlist Copy" },
            flag: "_hueScenePlaylistDuplicating"
        },
        {
            method: "renameScenePlaylist",
            response: { name: "Renamed Playlist" },
            flag: "_hueScenePlaylistRenaming"
        },
        {
            method: "deleteScenePlaylist",
            response: null,
            flag: "_hueScenePlaylistDeleting",
            confirmation: true
        }
    ];

    for (const testCase of cases) {
        const harness = makeHarness();
        const state = configureScenePlaylistMutationHarness(harness, testCase.method.replace("ScenePlaylist", ""));
        const { page, api, requests, dashboard } = harness;
        configureScenePlaylistMutationReloads(harness, state);
        api.clearScenePlaylist = () => {
            state.clearCalls += 1;
        };
        let confirmation;
        dashboard.confirm = (_message, _title, callback) => {
            confirmation = callback;
        };
        const operation = api[testCase.method](page);
        state.status.textContent = "unchanged after selection change";
        state.name.value = "current draft";
        state.select.value = "Playlist Two";
        if (testCase.confirmation) {
            confirmation(true);
            assert.equal(requests.length, 0, "selection-changed delete confirmation does not start a request");
        } else {
            assert.equal(requests.length, 1, `${testCase.method} starts one request before selection changes`);
            requests[0].resolve(testCase.response);
        }
        await operation;
        assert.equal(state.status.textContent, "unchanged after selection change", `${testCase.method} suppresses stale status after selection changes`);
        assert.equal(state.name.value, "current draft", `${testCase.method} preserves the current draft after selection changes`);
        assert.equal(state.playlistLoads, 0, `${testCase.method} suppresses stale playlist reload after selection changes`);
        assert.equal(state.scheduleLoads, 0, `${testCase.method} suppresses stale schedule reload after selection changes`);
        assert.equal(state.clearCalls, 0, `${testCase.method} suppresses stale form clearing after selection changes`);
        assert.equal(!!page[testCase.flag], false, `${testCase.method} clears its busy state after selection changes`);
        assert.equal(page._hueScenePlaylistMutation, null, `${testCase.method} releases its lock after selection changes`);
        for (const selector of scenePlaylistDirectMutationControlSelectors()) {
            assert.equal(page.querySelector(selector).disabled, false, `${testCase.method} restores ${selector} after selection changes`);
        }
    }
}

function configureScenePlaylistBulkHarness(harness) {
    const { page } = harness;
    const bulkSelect = page.querySelector("#scenePlaylistBulkSelect");
    bulkSelect.options = [
        { value: "playlist-1", selected: true },
        { value: "playlist-2", selected: true }
    ];
    Object.defineProperty(bulkSelect, "selectedOptions", {
        configurable: true,
        get() {
            return this.options.filter(option => option && option.selected);
        }
    });
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    return {
        button: page.querySelector("#deleteSelectedScenePlaylistsBtn"),
        duplicateButton: page.querySelector("#duplicateSelectedScenePlaylistsBtn"),
        status: page.querySelector("#scenePlaylistBulkStatus"),
        bulkSelect,
        playlistLoads: 0,
        scheduleLoads: 0
    };
}

function scenePlaylistBulkControlSelectors() {
    return scenePlaylistMutationControlSelectors();
}

async function testScenePlaylistBulkLifecycleGuards() {
    const confirmationHarness = makeHarness();
    const confirmationState = configureScenePlaylistBulkHarness(confirmationHarness);
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    let confirmationCalls = 0;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
        confirmationCalls += 1;
        confirmation = callback;
    };
    const confirmationOperation = confirmationApi.duplicateScenePlaylistsBulk(confirmationPage);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "bulk scene playlist duplicate returns a promise");
    assert.equal(confirmationHarness.requests.length, 0, "bulk scene playlist duplicate waits for confirmation before mutating configuration");
    assert.equal(confirmationCalls, 1, "bulk scene playlist duplicate asks for one confirmation");
    assert.equal(confirmationState.duplicateButton.disabled, true, "pending bulk scene playlist duplicate disables its button");
    for (const selector of scenePlaylistBulkControlSelectors()) {
        assert.equal(confirmationPage.querySelector(selector).disabled, true, `pending bulk scene playlist duplicate disables ${selector}`);
    }
    const pendingConfirmation = confirmationPage._hueScenePlaylistBulkDuplicateConfirmation;
    const duplicateConfirmation = confirmationApi.duplicateScenePlaylistsBulk(confirmationPage);
    const deleteSuppressed = confirmationApi.deleteScenePlaylistsBulk(confirmationPage);
    await Promise.all([duplicateConfirmation, deleteSuppressed]);
    assert.equal(confirmationCalls, 1, "duplicate and opposite bulk scene playlist actions do not open another confirmation");
    assert.equal(confirmationHarness.requests.length, 0, "duplicate and opposite bulk scene playlist actions do not submit before confirmation");
    let confirmationSettled = false;
    confirmationOperation.then(() => { confirmationSettled = true; });
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmationApi.beginPageLifecycle(confirmationPage);
    assert.equal(pendingConfirmation.canceled, true, "pagehide cancels pending bulk scene playlist duplicate confirmation");
    assert.equal(confirmationPage._hueScenePlaylistMutation, null, "pagehide releases pending bulk scene playlist duplicate lock");
    for (const selector of scenePlaylistBulkControlSelectors()) {
        assert.equal(confirmationPage.querySelector(selector).disabled, false, `pagehide restores ${selector} after bulk scene playlist duplicate confirmation`);
    }
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(confirmationSettled, true, "pagehide settles bulk scene playlist duplicate confirmation when its dialog callback never runs");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "stale bulk scene playlist duplicate confirmation cannot start a request after pagehide");
    await confirmationOperation;

    const staleHarness = makeHarness();
    const staleState = configureScenePlaylistBulkHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadScenePlaylists = () => {
        staleState.playlistLoads += 1;
        return Promise.resolve();
    };
    staleApi.loadSceneSchedules = () => {
        staleState.scheduleLoads += 1;
        return Promise.resolve();
    };
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.deleteScenePlaylistsBulk(stalePage);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed bulk scene playlist delete starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "bulk scene playlist delete uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/ScenePlaylists/BulkDelete", "bulk scene playlist delete uses the bulk route");
    assert.deepEqual(JSON.parse(staleHarness.requests[0].options.data), {
        playlistIds: ["playlist-1", "playlist-2"]
    }, "bulk scene playlist delete snapshots selected playlist IDs");
    assert.ok(stalePage._huePageRequests.scenePlaylistBulkDelete, "bulk scene playlist delete is tracked by the page lifecycle");
    assert.equal(stalePage._hueScenePlaylistBulkDeleting, true, "bulk scene playlist delete marks the page busy");
    assert.equal(staleState.button.disabled, true, "bulk scene playlist delete keeps its button disabled while pending");
    staleState.status.textContent = "unchanged after pagehide";
    staleState.bulkSelect.options[0].selected = false;
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight bulk scene playlist delete");
    assert.equal(stalePage._huePageRequests.scenePlaylistBulkDelete, undefined, "pagehide removes bulk scene playlist delete request state");
    assert.equal(stalePage._hueScenePlaylistBulkDeleting, false, "pagehide clears bulk scene playlist delete busy state");
    assert.equal(stalePage._hueScenePlaylistMutation, null, "pagehide releases in-flight bulk scene playlist delete lock");
    for (const selector of scenePlaylistBulkControlSelectors()) {
        assert.equal(stalePage.querySelector(selector).disabled, false, `pagehide restores ${selector} after bulk scene playlist delete`);
    }
    staleHarness.requests[0].resolve({ deletedCount: 2 });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale bulk scene playlist delete cannot write hidden-page status");
    assert.equal(staleState.bulkSelect.options[0].selected, false, "stale bulk scene playlist delete cannot rewrite reused page selection");
    assert.equal(staleState.bulkSelect.options[1].selected, true, "stale bulk scene playlist delete preserves reused page selection");
    assert.equal(staleState.playlistLoads, 0, "stale bulk scene playlist delete cannot reload playlists");
    assert.equal(staleState.scheduleLoads, 0, "stale bulk scene playlist delete cannot reload schedules");

    const currentHarness = makeHarness();
    const currentState = configureScenePlaylistBulkHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadScenePlaylists = (_page, selectedName) => {
        currentState.playlistLoads += 1;
        assert.equal(selectedName, "Playlist One Copy", "current bulk scene playlist duplicate selects the first returned copy");
        return Promise.resolve();
    };
    currentApi.loadSceneSchedules = () => {
        currentState.scheduleLoads += 1;
        return Promise.resolve();
    };
    let currentConfirmation;
    let currentConfirmationCalls = 0;
    currentHarness.dashboard.confirm = (_message, _title, callback) => {
        currentConfirmationCalls += 1;
        currentConfirmation = callback;
    };
    const currentOperation = currentApi.duplicateScenePlaylistsBulk(currentPage);
    currentConfirmation(true);
    assert.equal(currentHarness.requests.length, 1, "current bulk scene playlist duplicate starts one request");
    assert.equal(currentHarness.requests[0].options.url, "HueSync/ScenePlaylists/BulkDuplicate", "bulk scene playlist duplicate uses the bulk route");
    const duplicateWhilePending = currentApi.duplicateScenePlaylistsBulk(currentPage);
    const deleteWhileDuplicatePending = currentApi.deleteScenePlaylistsBulk(currentPage);
    await Promise.all([duplicateWhilePending, deleteWhileDuplicatePending]);
    assert.equal(currentConfirmationCalls, 1, "pending bulk scene playlist duplicate suppresses duplicate and delete confirmation");
    assert.equal(currentHarness.requests.length, 1, "pending bulk scene playlist duplicate keeps one request in flight");
    assert.equal(currentPage._hueScenePlaylistMutation.key, "scenePlaylistBulkDuplicate", "bulk scene playlist duplicate owns the shared mutation lock");
    for (const selector of scenePlaylistBulkControlSelectors()) {
        assert.equal(currentPage.querySelector(selector).disabled, true, `pending bulk scene playlist duplicate keeps ${selector} disabled`);
    }
    currentApi.clearSelectedScenePlaylists(currentPage);
    currentApi.selectAllScenePlaylists(currentPage);
    assert.equal(currentState.bulkSelect.options[0].selected, true, "bulk scene playlist duplicate blocks clear-selection mutation");
    assert.equal(currentState.bulkSelect.options[1].selected, true, "bulk scene playlist duplicate blocks select-all mutation");
    currentHarness.requests[0].resolve({
        message: "Created two independent playlist copies atomically.",
        playlists: [{ name: "Playlist One Copy" }, { name: "Playlist Two Copy" }]
    });
    await currentOperation;
    assert.equal(currentState.status.textContent, "Created two independent playlist copies atomically.", "current bulk scene playlist duplicate reports success");
    assert.equal(currentState.bulkSelect.options[0].selected, false, "current bulk scene playlist duplicate clears the first selection");
    assert.equal(currentState.bulkSelect.options[1].selected, false, "current bulk scene playlist duplicate clears the second selection");
    assert.equal(currentState.playlistLoads, 1, "current bulk scene playlist duplicate reloads playlists once");
    assert.equal(currentState.scheduleLoads, 1, "current bulk scene playlist duplicate reloads schedules once");
    assert.equal(currentPage._hueScenePlaylistBulkDuplicating, false, "current bulk scene playlist duplicate clears busy state");
    assert.equal(currentPage._hueScenePlaylistMutation, null, "current bulk scene playlist duplicate releases shared mutation lock");
    assert.equal(currentState.duplicateButton.disabled, true, "current bulk scene playlist duplicate leaves duplicate disabled with no selection");
    assert.equal(currentPage.querySelector("#deleteSelectedScenePlaylistsBtn").disabled, true, "current bulk scene playlist duplicate leaves delete disabled with no selection");
    assert.equal(currentPage._huePageRequests.scenePlaylistBulkDuplicate, undefined, "current bulk scene playlist duplicate removes settled lifecycle state");

    const deleteHarness = makeHarness();
    const deleteState = configureScenePlaylistBulkHarness(deleteHarness);
    const deletePage = deleteHarness.page;
    const deleteApi = deleteHarness.api;
    deleteApi.loadScenePlaylists = () => {
        deleteState.playlistLoads += 1;
        return Promise.resolve();
    };
    deleteApi.loadSceneSchedules = () => {
        deleteState.scheduleLoads += 1;
        return Promise.resolve();
    };
    let deleteConfirmation;
    deleteHarness.dashboard.confirm = (_message, _title, callback) => { deleteConfirmation = callback; };
    const deleteOperation = deleteApi.deleteScenePlaylistsBulk(deletePage);
    deleteConfirmation(true);
    assert.equal(deleteHarness.requests.length, 1, "current bulk scene playlist delete starts one request");
    assert.equal(deleteHarness.requests[0].options.url, "HueSync/ScenePlaylists/BulkDelete", "current bulk scene playlist delete uses the bulk route");
    deleteHarness.requests[0].resolve({
        message: "Deleted 2 scene playlist(s); scheduled-cue references were checked atomically.",
        deletedCount: 2
    });
    await deleteOperation;
    assert.equal(deleteState.status.textContent, "Deleted 2 scene playlist(s); scheduled-cue references were checked atomically.", "current bulk scene playlist delete reports success");
    assert.equal(deleteState.bulkSelect.options[0].selected, false, "current bulk scene playlist delete clears the first selection");
    assert.equal(deleteState.bulkSelect.options[1].selected, false, "current bulk scene playlist delete clears the second selection");
    assert.equal(deleteState.playlistLoads, 1, "current bulk scene playlist delete reloads playlists once");
    assert.equal(deleteState.scheduleLoads, 1, "current bulk scene playlist delete reloads schedules once");
    assert.equal(deletePage._hueScenePlaylistBulkDeleting, false, "current bulk scene playlist delete clears busy state");
    assert.equal(deletePage._hueScenePlaylistMutation, null, "current bulk scene playlist delete releases shared mutation lock");
    assert.equal(deleteState.button.disabled, true, "current bulk scene playlist delete leaves delete disabled with no selection");
    assert.equal(deleteState.duplicateButton.disabled, true, "current bulk scene playlist delete leaves duplicate disabled with no selection");
    assert.equal(deletePage._huePageRequests.scenePlaylistBulkDelete, undefined, "current bulk scene playlist delete removes settled lifecycle state");
}

async function testScenePlaylistSharedMutationLockArbitration() {
    const bulkHarness = makeHarness();
    const bulkState = configureScenePlaylistBulkHarness(bulkHarness);
    configureScenePlaylistMutationHarness(bulkHarness, "rename");
    const bulkPage = bulkHarness.page;
    const bulkApi = bulkHarness.api;
    bulkApi.loadScenePlaylists = () => {
        bulkState.playlistLoads += 1;
        return Promise.resolve();
    };
    bulkApi.loadSceneSchedules = () => {
        bulkState.scheduleLoads += 1;
        return Promise.resolve();
    };
    let bulkConfirmation;
    let bulkConfirmationCalls = 0;
    bulkHarness.dashboard.confirm = (_message, _title, callback) => {
        bulkConfirmationCalls += 1;
        bulkConfirmation = callback;
    };

    const bulkOperation = bulkApi.duplicateScenePlaylistsBulk(bulkPage);
    assert.equal(bulkPage._hueScenePlaylistMutation.key, "scenePlaylistBulkDuplicate", "bulk playlist mutation owns the unified lock");
    for (const selector of scenePlaylistMutationControlSelectors()) {
        assert.equal(bulkPage.querySelector(selector).disabled, true, `bulk playlist mutation disables ${selector} before confirmation`);
    }
    const directWhileBulk = bulkApi.renameScenePlaylist(bulkPage);
    await directWhileBulk;
    assert.equal(bulkConfirmationCalls, 1, "direct playlist mutation does not open a confirmation while bulk mutation owns the lock");
    assert.equal(bulkHarness.requests.length, 0, "direct playlist mutation does not submit while bulk confirmation is pending");
    assert.equal(bulkPage._hueScenePlaylistMutation.key, "scenePlaylistBulkDuplicate", "direct playlist mutation cannot replace bulk lock ownership");

    bulkConfirmation(true);
    assert.equal(bulkHarness.requests.length, 1, "bulk playlist mutation starts its request after confirmation");
    bulkState.status.textContent = "unchanged after pagehide";
    bulkApi.invalidatePageLifecycle(bulkPage);
    bulkApi.beginPageLifecycle(bulkPage);
    assert.equal(bulkHarness.requests[0].promise.aborted, true, "pagehide aborts the bulk request under the unified lock");
    assert.equal(bulkPage._hueScenePlaylistMutation, null, "pagehide releases the unified bulk playlist lock");
    for (const selector of scenePlaylistMutationControlSelectors()) {
        assert.equal(bulkPage.querySelector(selector).disabled, false, `pagehide restores ${selector} after bulk mutation teardown`);
    }
    bulkHarness.requests[0].resolve({ message: "stale bulk duplicate", playlists: [{ name: "Stale Copy" }] });
    await bulkOperation;
    assert.equal(bulkState.status.textContent, "unchanged after pagehide", "stale bulk completion cannot update the reused page");
    assert.equal(bulkState.playlistLoads, 0, "stale bulk completion cannot reload playlists");
    assert.equal(bulkState.scheduleLoads, 0, "stale bulk completion cannot reload schedules");

    const directHarness = makeHarness();
    const directState = configureScenePlaylistMutationHarness(directHarness, "duplicate");
    configureScenePlaylistBulkHarness(directHarness);
    const directPage = directHarness.page;
    const directApi = directHarness.api;
    directApi.loadScenePlaylists = () => {
        directState.playlistLoads += 1;
        return Promise.resolve();
    };
    directApi.loadSceneSchedules = () => {
        directState.scheduleLoads += 1;
        return Promise.resolve();
    };
    let directConfirmationCalls = 0;
    directHarness.dashboard.confirm = () => {
        directConfirmationCalls += 1;
    };

    const directOperation = directApi.duplicateScenePlaylist(directPage);
    assert.equal(directHarness.requests.length, 1, "direct playlist mutation starts its request");
    assert.equal(directPage._hueScenePlaylistMutation.key, "scenePlaylistDuplicate", "direct playlist mutation owns the unified lock");
    const bulkWhileDirect = directApi.deleteScenePlaylistsBulk(directPage);
    await bulkWhileDirect;
    assert.equal(directConfirmationCalls, 0, "bulk playlist mutation does not open a confirmation while direct mutation owns the lock");
    assert.equal(directHarness.requests.length, 1, "bulk playlist mutation does not submit while direct mutation is pending");
    assert.equal(directPage._hueScenePlaylistMutation.key, "scenePlaylistDuplicate", "bulk playlist mutation cannot replace direct lock ownership");
    for (const selector of scenePlaylistMutationControlSelectors()) {
        assert.equal(directPage.querySelector(selector).disabled, true, `direct playlist mutation keeps ${selector} disabled during bulk arbitration`);
    }

    directState.status.textContent = "unchanged after pagehide";
    directApi.invalidatePageLifecycle(directPage);
    directApi.beginPageLifecycle(directPage);
    assert.equal(directHarness.requests[0].promise.aborted, true, "pagehide aborts the direct request under the unified lock");
    assert.equal(directPage._hueScenePlaylistMutation, null, "pagehide releases the unified direct playlist lock");
    for (const selector of scenePlaylistMutationControlSelectors()) {
        assert.equal(directPage.querySelector(selector).disabled, false, `pagehide restores ${selector} after direct mutation teardown`);
    }
    directHarness.requests[0].resolve({ name: "Stale Copy" });
    await directOperation;
    assert.equal(directState.status.textContent, "unchanged after pagehide", "stale direct completion cannot update the reused page");
    assert.equal(directState.playlistLoads, 0, "stale direct completion cannot reload playlists");
    assert.equal(directState.scheduleLoads, 0, "stale direct completion cannot reload schedules");
}

function configureSceneScheduleSaveHarness(harness) {
    const { page, api } = harness;
    api.requireSceneScheduleMetadata = () => true;
    page._hueSceneScheduleMetadataReady = true;
    api.getSceneScheduleTargetSelection = () => ({
        valid: true,
        targetAllEnabledMappings: false,
        includeDefaultTarget: false,
        targetUserIds: [],
        targetRoutes: [],
        targetUserId: ""
    });
    const days = page.querySelector("#sceneScheduleDays");
    days.options = [{ value: "1", selected: true }];
    page.querySelector("#sceneScheduleName").value = "Cue One";
    page.querySelector("#sceneScheduleSourceType").value = "scene";
    page.querySelector("#sceneSchedulePresetSelect").value = "Scene One";
    page.querySelector("#sceneScheduleTimeMode").value = "Fixed";
    page.querySelector("#sceneScheduleTime").value = "20:00";
    page.querySelector("#sceneSchedulePriority").value = "0";
    page.querySelector("#sceneSchedulePlaybackPolicy").value = "Inherit";
    page.querySelector("#sceneScheduleRecurrence").value = "Weekly";
    page.querySelector("#sceneScheduleRecurrenceInterval").value = "1";
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    page.querySelector("#sceneScheduleEnabled").checked = true;
    return {
        button: page.querySelector("#saveSceneScheduleBtn"),
        status: page.querySelector("#sceneScheduleStatus"),
        scheduleLoads: 0,
        buttonUpdates: 0
    };
}

async function testSceneScheduleSaveLifecycleGuards() {
    const staleHarness = makeHarness();
    const staleState = configureSceneScheduleSaveHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    staleApi.loadSceneSchedules = () => {
        staleState.scheduleLoads += 1;
        return Promise.resolve();
    };
    staleApi.updateSceneScheduleButtons = () => {
        staleState.buttonUpdates += 1;
    };

    const staleSave = staleApi.saveSceneSchedule(stalePage);
    assert.ok(staleSave && typeof staleSave.then === "function", "scene schedule save returns a tracked promise");
    assert.equal(staleHarness.requests.length, 1, "scene schedule save starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "scene schedule save uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/SceneSchedules", "scene schedule save targets the schedule endpoint");
    assert.equal(stalePage._hueSceneScheduleSaving, true, "scene schedule save marks itself busy");
    assert.equal(staleState.button.disabled, true, "scene schedule save disables its button");
    assert.ok(stalePage._huePageRequests.sceneScheduleSave, "scene schedule save is tracked by the page lifecycle");

    staleState.status.textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts scene schedule save");
    assert.equal(stalePage._huePageRequests.sceneScheduleSave, undefined, "pagehide removes scene schedule save state");
    assert.equal(stalePage._hueSceneScheduleSaving, false, "pagehide clears scene schedule save state");
    assert.equal(staleState.button.disabled, false, "pagehide re-enables the scene schedule save button");
    staleHarness.requests[0].resolve({ id: "stale-cue" });
    await staleSave;
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale scene schedule save cannot update hidden-page status");
    assert.equal(staleState.scheduleLoads, 0, "stale scene schedule save cannot reload schedules");
    assert.equal(staleState.buttonUpdates, 0, "stale scene schedule save cannot update current-page controls");
    assert.equal(staleHarness.dashboard.alerts.length, 0, "stale scene schedule save cannot show an alert");

    const currentHarness = makeHarness();
    const currentState = configureSceneScheduleSaveHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    currentApi.loadSceneSchedules = (_page, selectedId) => {
        currentState.scheduleLoads += 1;
        assert.equal(selectedId, "saved-cue", "current scene schedule save reloads the returned cue");
        return Promise.resolve();
    };
    currentApi.updateSceneScheduleButtons = () => {
        currentState.buttonUpdates += 1;
    };

    const currentSave = currentApi.saveSceneSchedule(currentPage);
    assert.equal(currentHarness.requests.length, 1, "current scene schedule save starts one request");
    currentHarness.requests[0].resolve({ id: "saved-cue" });
    await currentSave;
    assert.equal(currentState.status.textContent, "Scheduled cue 'Cue One' saved.", "current scene schedule save reports success");
    assert.equal(currentState.scheduleLoads, 1, "current scene schedule save reloads schedules once");
    assert.equal(currentPage._hueSceneScheduleSaving, false, "current scene schedule save clears busy state");
    assert.equal(currentState.button.disabled, false, "current scene schedule save re-enables its button");
    assert.equal(currentState.buttonUpdates, 1, "current scene schedule save refreshes current-page controls");
}

function configureSceneScheduleBulkMutationHarness(harness) {
    const { page, api } = harness;
    const bulkSelect = page.querySelector("#sceneScheduleBulkSelect");
    bulkSelect.options = [
        { value: "cue-1", selected: true },
        { value: "cue-2", selected: true }
    ];
    Object.defineProperty(bulkSelect, "selectedOptions", {
        configurable: true,
        get() {
            return this.options.filter(option => option && option.selected);
        }
    });
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    const state = {
        scheduleLoads: 0,
        buttonUpdates: 0,
        status: page.querySelector("#sceneScheduleBulkStatus"),
        bulkSelect,
        updateSceneScheduleBulkButtons: api.updateSceneScheduleBulkButtons
    };
    api.loadSceneSchedules = (_page, selectedId) => {
        state.scheduleLoads += 1;
        state.selectedId = selectedId;
        return Promise.resolve();
    };
    api.updateSceneScheduleBulkButtons = () => {
        state.buttonUpdates += 1;
    };
    return state;
}

function sceneScheduleBulkMutationControlSelectors() {
    return [
        "#sceneScheduleBulkSelect",
        "#selectAllSceneSchedulesBtn",
        "#clearSelectedSceneSchedulesBtn",
        "#enableSelectedSceneSchedulesBtn",
        "#disableSelectedSceneSchedulesBtn",
        "#skipSelectedSceneSchedulesBtn",
        "#clearSelectedSceneScheduleSkipsBtn",
        "#resetSelectedSceneScheduleRunCountsBtn",
        "#duplicateSelectedSceneSchedulesBtn",
        "#deleteSelectedSceneSchedulesBtn",
        "#runSelectedSceneSchedulesBtn",
        "#cancelSelectedSceneSchedulesBtn"
    ];
}

async function testSceneScheduleBulkMutationLifecycleGuards() {
    const cases = [
        {
            method: "setSceneSchedulesEnabledBulk",
            args: [true],
            key: "sceneScheduleBulkEnabled",
            opposite: "deleteSceneSchedulesBulk",
            route: "HueSync/SceneSchedules/BulkEnabled",
            response: { message: "Enabled selected scheduled cues." },
            success: "Enabled selected scheduled cues."
        },
        {
            method: "setSceneSchedulesSkipNextBulk",
            args: [true],
            key: "sceneScheduleBulkSkipNext",
            opposite: "resetSceneSchedulesRunCountsBulk",
            route: "HueSync/SceneSchedules/BulkSkipNext",
            response: { message: "Selected cue occurrences marked to skip." },
            success: "Selected cue occurrences marked to skip."
        },
        {
            method: "resetSceneSchedulesRunCountsBulk",
            args: [],
            key: "sceneScheduleBulkReset",
            opposite: "duplicateSceneSchedulesBulk",
            route: "HueSync/SceneSchedules/BulkResetRunCount",
            response: { message: "Selected counters reset." },
            success: "Selected counters reset.",
            staleReject: true
        },
        {
            method: "duplicateSceneSchedulesBulk",
            args: [],
            key: "sceneScheduleBulkDuplicate",
            opposite: "deleteSceneSchedulesBulk",
            route: "HueSync/SceneSchedules/BulkDuplicate",
            response: {
                message: "Disabled scheduled-cue copies created.",
                schedules: [{ id: "cue-copy" }]
            },
            success: "Disabled scheduled-cue copies created.",
            selectedId: "cue-copy"
        },
        {
            method: "deleteSceneSchedulesBulk",
            args: [],
            key: "sceneScheduleBulkDelete",
            opposite: "duplicateSceneSchedulesBulk",
            route: "HueSync/SceneSchedules/BulkDelete",
            response: { message: "Selected scheduled cues deleted; retained cue history was preserved." },
            success: "Selected scheduled cues deleted; retained cue history was preserved.",
            selectedId: ""
        }
    ];

    for (const testCase of cases) {
        const confirmationHarness = makeHarness();
        configureSceneScheduleBulkMutationHarness(confirmationHarness);
        const confirmationPage = confirmationHarness.page;
        const confirmationApi = confirmationHarness.api;
        let confirmation;
        let confirmationCalls = 0;
        confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
            confirmationCalls += 1;
            confirmation = callback;
        };
        const confirmationOperation = confirmationApi[testCase.method](confirmationPage, ...testCase.args);
        assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", `${testCase.method} returns a tracked promise`);
        assert.equal(confirmationCalls, 1, `${testCase.method} opens one confirmation`);
        assert.equal(confirmationHarness.requests.length, 0, `${testCase.method} waits for confirmation before mutating schedules`);
        for (const selector of sceneScheduleBulkMutationControlSelectors()) {
            assert.equal(confirmationPage.querySelector(selector).disabled, true, `pending ${testCase.method} disables ${selector}`);
        }
        const duplicateOperation = confirmationApi[testCase.method](confirmationPage, ...testCase.args);
        const oppositeOperation = confirmationApi[testCase.opposite](confirmationPage);
        await Promise.all([duplicateOperation, oppositeOperation]);
        assert.equal(confirmationCalls, 1, `${testCase.method} suppresses duplicate and opposite confirmations`);
        assert.equal(confirmationHarness.requests.length, 0, `${testCase.method} suppresses duplicate and opposite requests`);
        const pendingConfirmation = confirmationPage._hueSceneScheduleBulkConfirmation;
        confirmationApi.invalidatePageLifecycle(confirmationPage);
        assert.equal(pendingConfirmation.canceled, true, `pagehide cancels pending ${testCase.method} confirmation`);
        assert.equal(confirmationPage._hueSceneScheduleBulkMutation, null, `pagehide releases pending ${testCase.method} lock`);
        for (const selector of sceneScheduleBulkMutationControlSelectors()) {
            assert.equal(confirmationPage.querySelector(selector).disabled, false, `pagehide restores ${selector} after pending ${testCase.method}`);
        }
        confirmation(true);
        assert.equal(confirmationHarness.requests.length, 0, `stale ${testCase.method} confirmation cannot start a request`);
        await confirmationOperation;

        const staleHarness = makeHarness();
        const staleState = configureSceneScheduleBulkMutationHarness(staleHarness);
        const stalePage = staleHarness.page;
        const staleApi = staleHarness.api;
        let staleConfirmation;
        staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
        const staleOperation = staleApi[testCase.method](stalePage, ...testCase.args);
        staleConfirmation(true);
        assert.equal(staleHarness.requests.length, 1, `confirmed ${testCase.method} starts one request`);
        assert.equal(staleHarness.requests[0].options.type, "POST", `${testCase.method} uses POST`);
        assert.equal(staleHarness.requests[0].options.url, testCase.route, `${testCase.method} targets its bulk route`);
        assert.deepEqual(JSON.parse(staleHarness.requests[0].options.data).scheduleIds, ["cue-1", "cue-2"], `${testCase.method} snapshots selected schedule IDs`);
        staleState.status.textContent = "unchanged after pagehide";
        staleApi.invalidatePageLifecycle(stalePage);
        assert.equal(staleHarness.requests[0].promise.aborted, true, `pagehide aborts in-flight ${testCase.method}`);
        assert.equal(stalePage._huePageRequests[testCase.key], undefined, `pagehide removes ${testCase.method} lifecycle state`);
        assert.equal(stalePage._hueSceneScheduleBulkMutation, null, `pagehide releases in-flight ${testCase.method} lock`);
        if (testCase.staleReject) {
            staleHarness.requests[0].reject(new Error("stale scheduled-cue bulk mutation failure"));
        } else {
            staleHarness.requests[0].resolve(testCase.response);
        }
        await staleOperation;
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(staleState.status.textContent, "unchanged after pagehide", `stale ${testCase.method} cannot write status`);
        assert.equal(staleState.scheduleLoads, 0, `stale ${testCase.method} cannot reload schedules`);
        for (const selector of sceneScheduleBulkMutationControlSelectors()) {
            assert.equal(stalePage.querySelector(selector).disabled, false, `pagehide restores ${selector} after in-flight ${testCase.method}`);
        }

        const currentHarness = makeHarness();
        const currentState = configureSceneScheduleBulkMutationHarness(currentHarness);
        const currentPage = currentHarness.page;
        const currentApi = currentHarness.api;
        let currentConfirmation;
        let currentConfirmationCalls = 0;
        currentHarness.dashboard.confirm = (_message, _title, callback) => {
            currentConfirmationCalls += 1;
            currentConfirmation = callback;
        };
        const currentOperation = currentApi[testCase.method](currentPage, ...testCase.args);
        currentConfirmation(true);
        assert.equal(currentHarness.requests.length, 1, `current ${testCase.method} starts one request`);
        currentApi.runSceneSchedulesBulk(currentPage);
        assert.equal(currentHarness.requests.length, 1, `pending ${testCase.method} suppresses a conflicting bulk run`);
        const duplicateWhilePending = currentApi[testCase.method](currentPage, ...testCase.args);
        const oppositeWhilePending = currentApi[testCase.opposite](currentPage);
        await Promise.all([duplicateWhilePending, oppositeWhilePending]);
        assert.equal(currentConfirmationCalls, 1, `pending ${testCase.method} suppresses duplicate and opposite actions`);
        assert.equal(currentHarness.requests.length, 1, `pending ${testCase.method} keeps one request in flight`);
        currentHarness.requests[0].resolve(testCase.response);
        await currentOperation;
        assert.equal(currentState.status.textContent, testCase.success, `current ${testCase.method} reports success`);
        assert.equal(currentState.scheduleLoads, 1, `current ${testCase.method} reloads schedules once`);
        assert.equal(currentState.selectedId, testCase.selectedId === undefined ? "cue-1" : testCase.selectedId, `current ${testCase.method} preserves the expected selection`);
        assert.equal(currentPage._hueSceneScheduleBulkMutation, null, `current ${testCase.method} releases its lock`);
        assert.equal(currentPage._huePageRequests[testCase.key], undefined, `current ${testCase.method} removes settled lifecycle state`);
        for (const selector of sceneScheduleBulkMutationControlSelectors()) {
            assert.equal(currentPage.querySelector(selector).disabled, false, `current ${testCase.method} restores ${selector}`);
        }
    }
}

function sceneScheduleDirectMutationControlSelectors() {
    return [
        "#sceneScheduleSelect",
        "#runSceneScheduleBtn",
        "#cancelSceneScheduleRunBtn",
        "#toggleSceneScheduleEnabledBtn",
        "#skipNextSceneScheduleBtn",
        "#duplicateSceneScheduleBtn",
        "#deleteSceneScheduleBtn",
        "#resetSceneScheduleRunCountBtn",
        "#clearSceneScheduleBtn",
        "#saveSceneScheduleBtn"
    ];
}

function sceneScheduleMutationControlSelectors() {
    return [...new Set([
        ...sceneScheduleDirectMutationControlSelectors(),
        ...sceneScheduleBulkMutationControlSelectors()
    ])];
}

function configureSceneScheduleDirectMutationHarness(harness) {
    const { page, api } = harness;
    const select = page.querySelector("#sceneScheduleSelect");
    select.value = "cue-1";
    select.selectedIndex = 0;
    select.options = [{ value: "cue-1", textContent: "Cue One" }];
    page._hueSceneSchedules = [{
        id: "cue-1",
        enabled: true,
        skipNextOccurrence: false
    }];
    page._hueSceneScheduleMetadataReady = true;
    const bulkSelect = page.querySelector("#sceneScheduleBulkSelect");
    bulkSelect.options = [{ value: "cue-1", selected: true }];
    Object.defineProperty(bulkSelect, "selectedOptions", {
        configurable: true,
        get() {
            return this.options.filter(option => option && option.selected);
        }
    });
    const state = {
        status: page.querySelector("#sceneScheduleStatus"),
        scheduleLoads: 0,
        selectedId: undefined
    };
    api.loadSceneSchedules = (_page, selectedId) => {
        state.scheduleLoads += 1;
        state.selectedId = selectedId;
        return Promise.resolve();
    };
    return state;
}

async function testSceneScheduleDirectMutationLifecycleGuards() {
    const cases = [
        {
            method: "duplicateSceneSchedule",
            key: "sceneScheduleDuplicate",
            route: "HueSync/SceneSchedules/cue-1/Duplicate",
            title: "Duplicate Cue",
            progress: "Duplicating scheduled cue...",
            response: { id: "cue-copy" },
            success: "Disabled cue copy created. Edit it, then enable it when ready.",
            selectedId: "cue-copy"
        },
        {
            method: "setSceneScheduleEnabled",
            key: "sceneScheduleEnabled",
            route: "HueSync/SceneSchedules/cue-1/Enabled",
            title: "Disable Cue",
            progress: "Disabling scheduled cue...",
            response: {},
            success: "Scheduled cue disabled without changing its schedule.",
            body: { enabled: false }
        },
        {
            method: "setSceneScheduleSkipNext",
            key: "sceneScheduleSkipNext",
            route: "HueSync/SceneSchedules/cue-1/SkipNext",
            title: "Skip Next Cue",
            progress: "Marking the next automatic cue to skip...",
            response: {},
            success: "The next automatic cue will be skipped; future recurrence is unchanged."
        },
        {
            method: "resetSceneScheduleRunCount",
            key: "sceneScheduleResetRunCount",
            route: "HueSync/SceneSchedules/cue-1/ResetRunCount",
            title: "Reset Run Counter",
            progress: "Resetting scheduled-cue execution counter...",
            response: {},
            success: "Scheduled-cue execution counter reset and cue re-enabled."
        }
    ];

    for (const testCase of cases) {
        const pendingHarness = makeHarness();
        configureSceneScheduleDirectMutationHarness(pendingHarness);
        const pendingPage = pendingHarness.page;
        const pendingApi = pendingHarness.api;
        let pendingConfirmation;
        let pendingConfirmationCalls = 0;
        pendingHarness.dashboard.confirm = (_message, title, callback) => {
            pendingConfirmationCalls += 1;
            assert.equal(title, testCase.title, `${testCase.method} uses its action-specific confirmation title`);
            pendingConfirmation = callback;
        };
        const pendingOperation = pendingApi[testCase.method](pendingPage);
        assert.ok(pendingOperation && typeof pendingOperation.then === "function", `${testCase.method} returns a tracked promise before confirmation`);
        assert.equal(pendingConfirmationCalls, 1, `${testCase.method} opens one confirmation`);
        assert.equal(pendingHarness.requests.length, 0, `${testCase.method} waits for confirmation before its request`);
        assert.equal(pendingPage._hueSceneScheduleMutation.key, testCase.key, `${testCase.method} owns the unified schedule mutation lock`);
        for (const selector of sceneScheduleMutationControlSelectors()) {
            assert.equal(pendingPage.querySelector(selector).disabled, true, `pending ${testCase.method} disables ${selector}`);
        }
        const blockedBulk = pendingApi.setSceneSchedulesEnabledBulk(pendingPage, true);
        await blockedBulk;
        pendingApi.runSceneSchedulesBulk(pendingPage);
        assert.equal(pendingConfirmationCalls, 1, `pending ${testCase.method} blocks bulk confirmation arbitration`);
        assert.equal(pendingHarness.requests.length, 0, `pending ${testCase.method} blocks bulk requests`);
        pendingApi.invalidatePageLifecycle(pendingPage);
        assert.equal(pendingPage._hueSceneScheduleMutation, null, `pagehide releases pending ${testCase.method} lock`);
        for (const selector of sceneScheduleMutationControlSelectors()) {
            assert.equal(pendingPage.querySelector(selector).disabled, false, `pagehide restores ${selector} after pending ${testCase.method}`);
        }
        pendingConfirmation(true);
        assert.equal(pendingHarness.requests.length, 0, `stale ${testCase.method} confirmation cannot submit a request`);
        await pendingOperation;

        const staleHarness = makeHarness();
        const staleState = configureSceneScheduleDirectMutationHarness(staleHarness);
        const stalePage = staleHarness.page;
        const staleApi = staleHarness.api;
        let staleConfirmation;
        staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
        const staleOperation = staleApi[testCase.method](stalePage);
        staleConfirmation(true);
        assert.equal(staleHarness.requests.length, 1, `confirmed ${testCase.method} starts one request`);
        assert.equal(staleHarness.requests[0].options.url, testCase.route, `${testCase.method} targets its selected cue`);
        if (testCase.body) {
            assert.deepEqual(JSON.parse(staleHarness.requests[0].options.data), testCase.body, `${testCase.method} sends its bounded request body`);
        }
        staleState.status.textContent = "unchanged after pagehide";
        staleApi.invalidatePageLifecycle(stalePage);
        assert.equal(staleHarness.requests[0].promise.aborted, true, `pagehide aborts in-flight ${testCase.method}`);
        assert.equal(stalePage._hueSceneScheduleMutation, null, `pagehide releases in-flight ${testCase.method} lock`);
        staleHarness.requests[0].resolve(testCase.response);
        await staleOperation;
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(staleState.status.textContent, "unchanged after pagehide", `stale ${testCase.method} cannot write status`);
        assert.equal(staleState.scheduleLoads, 0, `stale ${testCase.method} cannot reload schedules`);
        for (const selector of sceneScheduleMutationControlSelectors()) {
            assert.equal(stalePage.querySelector(selector).disabled, false, `pagehide restores ${selector} after in-flight ${testCase.method}`);
        }

        const currentHarness = makeHarness();
        const currentState = configureSceneScheduleDirectMutationHarness(currentHarness);
        const currentPage = currentHarness.page;
        const currentApi = currentHarness.api;
        let currentConfirmation;
        currentHarness.dashboard.confirm = (_message, title, callback) => {
            assert.equal(title, testCase.title, `current ${testCase.method} uses its action-specific confirmation title`);
            currentConfirmation = callback;
        };
        const currentOperation = currentApi[testCase.method](currentPage);
        currentConfirmation(true);
        assert.equal(currentHarness.requests.length, 1, `current ${testCase.method} starts one request`);
        assert.equal(currentHarness.requests[0].options.url, testCase.route, `current ${testCase.method} targets its selected cue`);
        currentState.status.textContent = testCase.progress;
        currentHarness.requests[0].resolve(testCase.response);
        await currentOperation;
        assert.equal(currentState.status.textContent, testCase.success, `current ${testCase.method} reports success`);
        assert.equal(currentState.scheduleLoads, 1, `current ${testCase.method} reloads schedules once`);
        assert.equal(currentState.selectedId, testCase.selectedId || "cue-1", `current ${testCase.method} preserves the expected selection`);
        assert.equal(currentPage._hueSceneScheduleMutation, null, `current ${testCase.method} releases the unified schedule mutation lock`);
        assert.equal(currentPage._hueSceneScheduleDirectMutation, null, `current ${testCase.method} clears its direct mutation alias`);
        assert.equal(currentPage._huePageRequests[testCase.key], undefined, `current ${testCase.method} removes settled lifecycle state`);
        for (const selector of sceneScheduleDirectMutationControlSelectors()) {
            assert.equal(currentPage.querySelector(selector).disabled, false, `current ${testCase.method} restores ${selector}`);
        }
    }
}

function configureSceneScheduleDeleteHarness(harness) {
    const { page } = harness;
    const select = page.querySelector("#sceneScheduleSelect");
    select.value = "cue-1";
    select.selectedIndex = 0;
    select.options = [{ value: "cue-1", textContent: "Cue One" }];
    return {
        button: page.querySelector("#deleteSceneScheduleBtn"),
        status: page.querySelector("#sceneScheduleStatus")
    };
}

async function testSceneScheduleDeleteLifecycleGuards() {
    const confirmationHarness = makeHarness();
    const confirmationState = configureSceneScheduleDeleteHarness(confirmationHarness);
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    let confirmationCalls = 0;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
        confirmationCalls += 1;
        confirmation = callback;
    };
    const confirmationOperation = confirmationApi.deleteSceneSchedule(confirmationPage);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "scene schedule delete returns a promise");
    assert.equal(confirmationHarness.requests.length, 0, "scene schedule delete waits for confirmation before mutating configuration");
    assert.equal(confirmationCalls, 1, "scene schedule delete asks for one confirmation");
    assert.equal(confirmationState.button.disabled, true, "pending scene schedule delete disables its button");
    const duplicateConfirmation = confirmationApi.deleteSceneSchedule(confirmationPage);
    assert.equal(confirmationCalls, 1, "duplicate scene schedule delete does not open another confirmation");
    assert.equal(confirmationHarness.requests.length, 0, "duplicate scene schedule delete does not submit before confirmation");
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmationApi.beginPageLifecycle(confirmationPage);
    assert.equal(confirmationState.button.disabled, false, "pagehide restores the pending scene schedule delete button");
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "stale scene schedule delete confirmation cannot start a request after pagehide");
    await confirmationOperation;
    await duplicateConfirmation;

    const staleHarness = makeHarness();
    const staleState = configureSceneScheduleDeleteHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    let staleScheduleLoads = 0;
    staleApi.loadSceneSchedules = () => {
        staleScheduleLoads += 1;
        return Promise.resolve();
    };
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.deleteSceneSchedule(stalePage);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed scene schedule delete starts one request");
    assert.equal(staleHarness.requests[0].options.type, "DELETE", "scene schedule delete uses DELETE");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/SceneSchedules/cue-1", "scene schedule delete scopes the request to the selected cue");
    assert.equal(staleHarness.requests[0].options.dataType, "json", "scene schedule delete accepts the API response safely");
    assert.ok(stalePage._huePageRequests.sceneScheduleDelete, "scene schedule delete is tracked by the page lifecycle");
    assert.equal(stalePage._hueSceneScheduleDeleting, true, "scene schedule delete marks the page busy");
    assert.equal(staleState.button.disabled, true, "scene schedule delete keeps its button disabled while pending");
    staleState.status.textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight scene schedule delete");
    assert.equal(stalePage._huePageRequests.sceneScheduleDelete, undefined, "pagehide removes the scene schedule delete request record");
    assert.equal(stalePage._hueSceneScheduleDeleting, false, "pagehide clears the scene schedule delete busy state");
    assert.equal(staleState.button.disabled, false, "pagehide restores the scene schedule delete button");
    staleHarness.requests[0].resolve({ message: "stale delete" });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale scene schedule delete cannot write hidden-page status");
    assert.equal(staleScheduleLoads, 0, "stale scene schedule delete cannot reload schedules");

    const currentHarness = makeHarness();
    const currentState = configureSceneScheduleDeleteHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    let currentScheduleLoads = 0;
    currentApi.loadSceneSchedules = () => {
        currentScheduleLoads += 1;
        return Promise.resolve();
    };
    let currentConfirmation;
    let currentConfirmationCalls = 0;
    currentHarness.dashboard.confirm = (_message, _title, callback) => {
        currentConfirmationCalls += 1;
        currentConfirmation = callback;
    };
    const currentOperation = currentApi.deleteSceneSchedule(currentPage);
    currentConfirmation(true);
    assert.equal(currentHarness.requests.length, 1, "current scene schedule delete starts one request");
    const duplicateWhilePending = currentApi.deleteSceneSchedule(currentPage);
    await duplicateWhilePending;
    assert.equal(currentConfirmationCalls, 1, "pending scene schedule delete blocks duplicate confirmation");
    assert.equal(currentHarness.requests.length, 1, "pending scene schedule delete keeps one request in flight");
    currentHarness.requests[0].resolve({ message: "deleted" });
    await currentOperation;
    assert.equal(currentState.status.textContent, "Scheduled cue deleted.", "current scene schedule delete reports success");
    assert.equal(currentScheduleLoads, 1, "current scene schedule delete reloads schedules once");
    assert.equal(currentPage._hueSceneScheduleDeleting, false, "current scene schedule delete clears the busy state");
    assert.equal(currentState.button.disabled, false, "current scene schedule delete restores its button");
    assert.equal(currentPage._huePageRequests.sceneScheduleDelete, undefined, "current scene schedule delete removes its settled lifecycle record");
}

async function testSceneScheduleRunCancellationUsesActiveId() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    const scheduleSelect = page.querySelector("#sceneScheduleSelect");
    scheduleSelect.value = "cue-a";

    const run = api.runSceneSchedule(page);
    assert.ok(run && typeof run.then === "function", "scheduled-cue run returns a tracked promise");
    assert.equal(requests.length, 1, "scheduled-cue run starts one request");
    assert.equal(requests[0].options.type, "POST", "scheduled-cue run uses POST");
    assert.equal(requests[0].options.url, "HueSync/SceneSchedules/cue-a/Run", "scheduled-cue run scopes the request to the selected cue");
    assert.equal(page._hueSceneScheduleActiveId, "cue-a", "scheduled-cue run stores its active schedule ID");
    assert.equal(scheduleSelect.disabled, true, "scheduled-cue run disables the schedule selector");
    assert.ok(page._huePageRequests.sceneScheduleRun, "scheduled-cue run is tracked by the page lifecycle");

    // Simulate a programmatic/stale selection change while the selector is disabled.
    scheduleSelect.value = "cue-b";
    const cancel = api.cancelSceneScheduleRun(page);
    assert.ok(cancel && typeof cancel.then === "function", "scheduled-cue cancel returns a tracked promise");
    assert.equal(requests.length, 2, "scheduled-cue cancel starts one request");
    assert.equal(
        requests[1].options.url,
        "HueSync/SceneSchedules/cue-a/Cancel",
        "scheduled-cue cancel uses the ID captured when the run started"
    );
    assert.equal(page._hueSceneScheduleActiveId, "cue-a", "cancel does not clear the active schedule ID early");
    assert.ok(page._huePageRequests.sceneScheduleCancellation, "scheduled-cue cancel is tracked by the page lifecycle");

    requests[1].resolve({ canceled: true });
    await cancel;
    assert.equal(page._hueSceneScheduleActiveId, "cue-a", "cancel completion leaves the run identity until the run completes");
    assert.equal(scheduleSelect.disabled, true, "schedule selector remains disabled while the run request is active");
    assert.equal(page._hueSceneScheduleCancellationRequest, null, "scheduled-cue cancel clears its request after completion");
    assert.equal(page._huePageRequests.sceneScheduleCancellation, undefined, "scheduled-cue cancel removes its lifecycle record after completion");

    requests[0].resolve({ succeeded: true, message: "Cue completed." });
    await run;
    assert.equal(page._hueSceneScheduleRequest, null, "matching run completion clears the active request");
    assert.equal(page._hueSceneScheduleActiveId, null, "matching run completion clears the active schedule ID");
    assert.equal(scheduleSelect.disabled, false, "matching run completion re-enables the schedule selector");
    assert.equal(page.querySelector("#sceneScheduleStatus").textContent, "Cue completed.", "current scheduled-cue run reports success");
    assert.equal(page._huePageRequests.sceneScheduleRun, undefined, "scheduled-cue run removes its lifecycle record after completion");

    const staleHarness = makeHarness();
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    const staleSelect = stalePage.querySelector("#sceneScheduleSelect");
    staleSelect.value = "cue-a";
    const staleRun = staleApi.runSceneSchedule(stalePage);
    staleSelect.value = "cue-b";
    const staleCancel = staleApi.cancelSceneScheduleRun(stalePage);
    assert.equal(staleHarness.requests.length, 2, "stale run/cancel scenario starts both requests");
    const staleRunRequest = staleHarness.requests[0];
    const staleCancelRequest = staleHarness.requests[1];
    stalePage.querySelector("#sceneScheduleStatus").textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleRunRequest.promise.aborted, true, "pagehide aborts the stale scheduled-cue run");
    assert.equal(staleCancelRequest.promise.aborted, true, "pagehide aborts the stale scheduled-cue cancellation");
    assert.equal(stalePage._huePageRequests.sceneScheduleRun, undefined, "pagehide removes stale scheduled-cue run state");
    assert.equal(stalePage._huePageRequests.sceneScheduleCancellation, undefined, "pagehide removes stale scheduled-cue cancellation state");
    assert.equal(stalePage._hueSceneScheduleRequest, null, "pagehide clears the stale scheduled-cue run pointer");
    assert.equal(stalePage._hueSceneScheduleCancellationRequest, null, "pagehide clears the stale scheduled-cue cancellation pointer");

    staleApi.beginPageLifecycle(stalePage);
    staleSelect.value = "cue-b";
    const currentRun = staleApi.runSceneSchedule(stalePage);
    const currentRunRequest = staleHarness.requests[2];
    const currentCancel = staleApi.cancelSceneScheduleRun(stalePage);
    const currentCancelRequest = staleHarness.requests[3];
    const currentRunPointer = stalePage._hueSceneScheduleRequest;
    const currentCancelPointer = stalePage._hueSceneScheduleCancellationRequest;
    assert.equal(currentRunRequest.options.url, "HueSync/SceneSchedules/cue-b/Run", "new scheduled-cue run uses the current selection after pagehide");
    assert.equal(currentCancelRequest.options.url, "HueSync/SceneSchedules/cue-b/Cancel", "new scheduled-cue cancellation uses its current run identity");
    stalePage.querySelector("#sceneScheduleStatus").textContent = "current lifecycle sentinel";

    staleCancelRequest.resolve({ canceled: true, message: "stale cancellation" });
    await staleCancel;
    assert.equal(stalePage._hueSceneScheduleCancellationRequest, currentCancelPointer, "stale cancellation finalizer cannot clear the current cancellation request");

    staleRunRequest.resolve({ succeeded: true, message: "stale run" });
    await staleRun;
    assert.equal(stalePage._hueSceneScheduleRequest, currentRunPointer, "stale run finalizer cannot clear the current run request");
    assert.equal(stalePage.querySelector("#sceneScheduleStatus").textContent, "current lifecycle sentinel", "stale run/cancel completions cannot overwrite the current page");

    currentCancelRequest.resolve({ canceled: true });
    await currentCancel;
    assert.equal(stalePage._hueSceneScheduleCancellationRequest, null, "current cancellation finalizer clears only its own request");
    assert.equal(stalePage._hueSceneScheduleRequest, currentRunPointer, "current cancellation leaves the active run identity intact");
    currentRunRequest.resolve({ succeeded: true, message: "current cue completed" });
    await currentRun;
    assert.equal(stalePage._hueSceneScheduleRequest, null, "current run finalizer clears its own request");
    assert.equal(stalePage._hueSceneScheduleActiveId, null, "current run finalizer clears its own active identity");
    assert.equal(stalePage.querySelector("#sceneScheduleStatus").textContent, "current cue completed", "current scheduled-cue run can update status after stale callbacks");

    const barrierHarness = makeHarness();
    const barrierPage = barrierHarness.page;
    const barrierApi = barrierHarness.api;
    barrierPage.querySelector("#sceneScheduleSelect").value = "cue-a";
    const barrierRun = barrierApi.runSceneSchedule(barrierPage);
    const barrierRunRequest = barrierHarness.requests[0];
    const barrierCancel = barrierApi.cancelSceneScheduleRun(barrierPage);
    const barrierCancelRequest = barrierHarness.requests[1];
    barrierRunRequest.resolve({ succeeded: true, message: "run finished before cancellation" });
    await barrierRun;
    assert.equal(barrierPage._hueSceneScheduleRequest, null, "a completed run releases its run pointer before cancellation settles");
    assert.equal(
        barrierPage._hueSceneScheduleCancellationRequest._huePageRequestRecord.request,
        barrierCancelRequest.promise,
        "a pending cancellation remains the shared operation barrier"
    );
    const blockedRun = barrierApi.runSceneSchedule(barrierPage);
    const blockedMutation = barrierApi.setSceneScheduleEnabled(barrierPage);
    await Promise.all([blockedRun, blockedMutation]);
    assert.equal(barrierHarness.requests.length, 2, "a pending individual cancellation blocks a replacement run and configuration mutation");
    barrierCancelRequest.resolve({ canceled: true });
    await barrierCancel;
    const replacementRun = barrierApi.runSceneSchedule(barrierPage);
    assert.equal(barrierHarness.requests.length, 3, "a replacement run can start after the cancellation barrier settles");
    barrierHarness.requests[2].resolve({ succeeded: true, message: "replacement completed" });
    await replacementRun;
}

async function testSceneScheduleBulkRunLifecycleGuards() {
    const confirmationHarness = makeHarness();
    configureSceneScheduleBulkMutationHarness(confirmationHarness);
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    let confirmation;
    let confirmationCalls = 0;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => {
        confirmationCalls += 1;
        confirmation = callback;
    };
    const confirmationOperation = confirmationApi.runSceneSchedulesBulk(confirmationPage);
    assert.ok(confirmationOperation && typeof confirmationOperation.then === "function", "bulk scheduled-cue run returns a tracked confirmation promise");
    assert.equal(confirmationCalls, 1, "bulk scheduled-cue run opens one confirmation");
    assert.equal(confirmationHarness.requests.length, 0, "bulk scheduled-cue run waits for confirmation");
    assert.equal(confirmationPage._hueSceneScheduleBulkMutation.key, "sceneScheduleBulkRun", "bulk scheduled-cue run owns its mutation lock during confirmation");
    for (const selector of sceneScheduleMutationControlSelectors()) {
        assert.equal(confirmationPage.querySelector(selector).disabled, true, `pending bulk scheduled-cue run disables ${selector}`);
    }
    const duplicateConfirmation = confirmationApi.runSceneSchedulesBulk(confirmationPage);
    const oppositeConfirmation = confirmationApi.setSceneSchedulesEnabledBulk(confirmationPage, true);
    await Promise.all([duplicateConfirmation, oppositeConfirmation]);
    assert.equal(confirmationCalls, 1, "pending bulk scheduled-cue run suppresses duplicate and opposite confirmations");
    assert.equal(confirmationHarness.requests.length, 0, "pending bulk scheduled-cue run suppresses duplicate and opposite requests");
    const pendingConfirmation = confirmationPage._hueSceneScheduleBulkConfirmation;
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    assert.equal(pendingConfirmation.canceled, true, "pagehide cancels pending bulk scheduled-cue run confirmation");
    assert.equal(confirmationPage._hueSceneScheduleBulkMutation, null, "pagehide releases the pending bulk scheduled-cue run lock");
    for (const selector of sceneScheduleMutationControlSelectors()) {
        assert.equal(confirmationPage.querySelector(selector).disabled, false, `pagehide restores ${selector} after pending bulk scheduled-cue run`);
    }
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "stale bulk scheduled-cue run confirmation cannot submit a request");
    await confirmationOperation;

    const staleHarness = makeHarness();
    const staleState = configureSceneScheduleBulkMutationHarness(staleHarness);
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    let staleConfirmation;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirmation = callback; };
    const staleOperation = staleApi.runSceneSchedulesBulk(stalePage);
    staleConfirmation(true);
    assert.equal(staleHarness.requests.length, 1, "confirmed bulk scheduled-cue run starts one request");
    assert.equal(staleHarness.requests[0].options.type, "POST", "bulk scheduled-cue run uses POST");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/SceneSchedules/BulkRun", "bulk scheduled-cue run targets the bulk run route");
    assert.deepEqual(JSON.parse(staleHarness.requests[0].options.data), { scheduleIds: ["cue-1", "cue-2"] }, "bulk scheduled-cue run snapshots selected IDs");
    assert.ok(stalePage._huePageRequests.sceneScheduleBulkRun, "bulk scheduled-cue run is tracked by the page lifecycle");
    staleState.status.textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight bulk scheduled-cue run");
    assert.equal(stalePage._huePageRequests.sceneScheduleBulkRun, undefined, "pagehide removes bulk scheduled-cue run state");
    assert.equal(stalePage._hueSceneScheduleBulkRequest, null, "pagehide clears the bulk scheduled-cue run pointer");
    staleHarness.requests[0].resolve({ message: "stale bulk run" });
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(staleState.status.textContent, "unchanged after pagehide", "stale bulk scheduled-cue completion cannot overwrite the page");
    assert.equal(staleState.scheduleLoads, 0, "stale bulk scheduled-cue completion cannot reload schedules");

    const currentHarness = makeHarness();
    const currentState = configureSceneScheduleBulkMutationHarness(currentHarness);
    const currentPage = currentHarness.page;
    const currentApi = currentHarness.api;
    let currentConfirmation;
    let currentConfirmationCalls = 0;
    currentHarness.dashboard.confirm = (_message, _title, callback) => {
        currentConfirmationCalls += 1;
        currentConfirmation = callback;
    };
    const currentOperation = currentApi.runSceneSchedulesBulk(currentPage);
    currentConfirmation(true);
    assert.equal(currentHarness.requests.length, 1, "current bulk scheduled-cue run starts one request");
    const duplicateWhilePending = currentApi.runSceneSchedulesBulk(currentPage);
    await duplicateWhilePending;
    assert.equal(currentConfirmationCalls, 1, "in-flight bulk scheduled-cue run suppresses duplicate confirmation");
    assert.equal(currentHarness.requests.length, 1, "in-flight bulk scheduled-cue run keeps one request");
    currentHarness.requests[0].resolve({
        message: "Selected scheduled cues completed.",
        results: [{ scheduleName: "Cue One", succeeded: true }]
    });
    await currentOperation;
    assert.equal(currentState.status.textContent, "Selected scheduled cues completed. Cue One: succeeded", "current bulk scheduled-cue run reports its result");
    assert.equal(currentState.scheduleLoads, 1, "current bulk scheduled-cue run reloads schedules once");
    assert.equal(currentState.selectedId, "cue-1", "current bulk scheduled-cue run preserves the selected cue");
    assert.equal(currentPage._hueSceneScheduleBulkRequest, null, "current bulk scheduled-cue run clears its request pointer");
    assert.equal(currentPage._hueSceneScheduleBulkActiveIds, null, "current bulk scheduled-cue run clears its active IDs");
    assert.equal(currentPage._huePageRequests.sceneScheduleBulkRun, undefined, "current bulk scheduled-cue run removes its lifecycle record");
    assert.equal(currentPage.querySelector("#runSelectedSceneSchedulesBtn").disabled, false, "current bulk scheduled-cue run restores the run button");
    currentState.updateSceneScheduleBulkButtons(currentPage);
    assert.equal(currentPage.querySelector("#cancelSelectedSceneSchedulesBtn").disabled, true, "current bulk scheduled-cue run disables cancel after completion");

    const barrierHarness = makeHarness();
    configureSceneScheduleBulkMutationHarness(barrierHarness);
    const barrierPage = barrierHarness.page;
    const barrierApi = barrierHarness.api;
    let barrierConfirmation;
    barrierHarness.dashboard.confirm = (_message, _title, callback) => { barrierConfirmation = callback; };
    const barrierRun = barrierApi.runSceneSchedulesBulk(barrierPage);
    barrierConfirmation(true);
    const barrierRunRequest = barrierHarness.requests[0];
    const barrierCancel = barrierApi.cancelSceneSchedulesBulk(barrierPage);
    const barrierCancelRequest = barrierHarness.requests[1];
    barrierRunRequest.resolve({ message: "bulk run finished before cancellation" });
    await barrierRun;
    assert.equal(barrierPage._hueSceneScheduleBulkRequest, null, "a completed bulk run releases its run pointer before cancellation settles");
    assert.equal(
        barrierPage._hueSceneScheduleBulkCancellationRequest._huePageRequestRecord.request,
        barrierCancelRequest.promise,
        "a pending bulk cancellation remains the shared operation barrier"
    );
    const blockedBulkRun = barrierApi.runSceneSchedulesBulk(barrierPage);
    const blockedBulkMutation = barrierApi.setSceneSchedulesEnabledBulk(barrierPage, true);
    await Promise.all([blockedBulkRun, blockedBulkMutation]);
    assert.equal(barrierHarness.requests.length, 2, "a pending bulk cancellation blocks replacement runs and configuration mutations");
    barrierCancelRequest.resolve({ canceledCount: 1 });
    await barrierCancel;
    const replacementBulkRun = barrierApi.runSceneSchedulesBulk(barrierPage);
    barrierConfirmation(true);
    assert.equal(barrierHarness.requests.length, 3, "a replacement bulk run can start after the cancellation barrier settles");
    barrierHarness.requests[2].resolve({ message: "replacement bulk run completed" });
    await replacementBulkRun;
}

async function testSceneScheduleReloadLeaseGuardsActions() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    page.querySelector("#sceneScheduleSelect").value = "cue-1";
    page.querySelector("#sceneScheduleSelect").options = [{ value: "cue-1", textContent: "Cue One" }];

    const reload = api.loadSceneSchedules(page, "cue-1");
    assert.ok(reload && typeof reload.then === "function", "scene schedule reload returns a promise");
    assert.ok(page._hueSceneScheduleReloadLease, "scene schedule reload owns a busy lease while metadata is pending");
    assert.equal(page.querySelector("#sceneScheduleSelect").disabled, true, "scene schedule reload disables the direct selector");
    assert.equal(page.querySelector("#saveSceneScheduleBtn").disabled, true, "scene schedule reload disables direct saves");
    assert.equal(page.querySelector("#runSelectedSceneSchedulesBtn").disabled, true, "scene schedule reload disables bulk runs");
    const requestCountBeforeBlockedActions = requests.length;
    await Promise.all([
        api.runSceneSchedule(page),
        api.runSceneSchedulesBulk(page),
        api.saveSceneSchedule(page)
    ]);
    assert.equal(requests.length, requestCountBeforeBlockedActions, "scene schedule reload blocks direct and bulk mutation/run submissions");

    // getPageLifecycleRequest creates the time-zone request first, followed by
    // schedules, presets, playlists, and mapping metadata.
    requests[0].resolve([]);
    requests[1].resolve([]);
    requests[2].resolve([]);
    requests[3].resolve([]);
    requests[4].resolve([]);
    await reload;
    assert.equal(page._hueSceneScheduleReloadLease, null, "scene schedule reload releases its lease after metadata settles");
    assert.equal(page.querySelector("#sceneScheduleSelect").disabled, false, "scene schedule reload restores the direct selector");
    assert.equal(page.querySelector("#sceneScheduleBulkSelect").disabled, false, "scene schedule reload restores the bulk selector");

    // The reload's runtime-status follow-up is independently tracked; tear it
    // down so the harness cannot retain a background request.
    api.invalidatePageLifecycle(page);
}

async function testEntertainmentAreaSelectionHandlesUnsafeIds() {
    const harness = makeHarness();
    const { page, api } = harness;
    const select = page.querySelector("#entertainmentAreaSelect");
    const unsafeId = 'area"]\'quoted';
    select.options = [{ value: unsafeId, textContent: "Unsafe area" }];
    select.querySelector = () => {
        throw new Error("selector interpolation should not be used for area IDs");
    };

    assert.doesNotThrow(() => api.syncSelectedArea(page, unsafeId));
    assert.equal(select.value, unsafeId, "area selection matches the exact persisted ID");
}

async function testPreviewLifecyclePagehideGuards() {
    const previewHarness = makeHarness();
    const previewPage = previewHarness.page;
    const previewApi = previewHarness.api;
    const previewStatus = previewPage.querySelector("#previewColorStatus");
    let previewHideCount = 0;
    previewHarness.dashboard.hideLoadingMsg = () => { previewHideCount += 1; };
    previewPage._huePreviewTargetMetadataReady = true;
    previewApi.getPreviewValues = () => ({
        red: 1,
        green: 2,
        blue: 3,
        brightnessPercent: 80,
        effectSpeedPercent: 100,
        durationSeconds: 5,
        effect: "Solid",
        transitionSeconds: 0,
        transitionOutSeconds: 0,
        transitionCurve: "Linear"
    });

    previewApi.previewAllEnabledTargets(previewPage);
    assert.equal(previewHarness.requests.length, 1, "all-target preview starts one request");
    const stalePreviewRequest = previewHarness.requests[0];
    assert.ok(previewPage._huePageRequests.preview, "all-target preview is tracked by page lifecycle");
    previewApi.invalidatePageLifecycle(previewPage);
    assert.equal(stalePreviewRequest.promise.aborted, true, "pagehide aborts the all-target preview request");
    assert.equal(previewPage._huePreviewRequest, null, "pagehide clears the all-target preview request");
    const previewHideAfterPagehide = previewHideCount;

    previewApi.beginPageLifecycle(previewPage);
    previewPage._huePreviewTargetMetadataReady = true;
    previewApi.previewAllEnabledTargets(previewPage);
    assert.equal(previewHarness.requests.length, 2, "a new all-target preview can start after pagehide");
    const currentPreviewRequest = previewHarness.requests[1];
    previewStatus.textContent = "current preview sentinel";
    stalePreviewRequest.resolve({ succeeded: true, message: "stale preview" });
    await stalePreviewRequest.promise;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(previewStatus.textContent, "current preview sentinel", "stale preview completion cannot overwrite the current page");
    assert.equal(previewHideCount, previewHideAfterPagehide, "stale preview completion cannot hide current-page loading state");

    currentPreviewRequest.resolve({ succeeded: true, message: "current preview" });
    await currentPreviewRequest.promise;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(previewPage._huePreviewRequest, null, "current preview completion clears its request");
    assert.equal(previewHideCount, previewHideAfterPagehide + 1, "current preview completion hides loading state once");

    const captureHarness = makeHarness();
    const capturePage = captureHarness.page;
    const captureApi = captureHarness.api;
    const captureStatus = capturePage.querySelector("#previewColorStatus");
    const captureColor = capturePage.querySelector("#previewColor");
    const captureBrightness = capturePage.querySelector("#previewBrightness");
    let captureHideCount = 0;
    captureHarness.dashboard.hideLoadingMsg = () => { captureHideCount += 1; };
    capturePage._huePreviewTargetMetadataReady = true;
    captureApi.getCurrentLightCaptureTargetSelection = () => ({
        targetAllEnabledMappings: false,
        includeDefaultTarget: false,
        targetUserIds: [],
        targetRoutes: [],
        targetUserId: "",
        targetDeviceId: ""
    });

    captureApi.captureCurrentColor(capturePage);
    assert.equal(captureHarness.requests.length, 1, "current-light capture starts one request");
    const staleCaptureRequest = captureHarness.requests[0];
    captureApi.invalidatePageLifecycle(capturePage);
    assert.equal(staleCaptureRequest.promise.aborted, true, "pagehide aborts the current-light capture request");
    const captureHideAfterPagehide = captureHideCount;
    captureApi.beginPageLifecycle(capturePage);
    capturePage._huePreviewTargetMetadataReady = true;
    captureApi.captureCurrentColor(capturePage);
    assert.equal(captureHarness.requests.length, 2, "a new current-light capture can start after pagehide");
    const currentCaptureRequest = captureHarness.requests[1];
    captureColor.value = "#102030";
    captureBrightness.value = "20";
    captureStatus.textContent = "current capture sentinel";
    staleCaptureRequest.resolve({ Succeeded: true, Red: 255, Green: 0, Blue: 0, BrightnessPercent: 99 });
    await staleCaptureRequest.promise;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(captureColor.value, "#102030", "stale capture completion cannot overwrite the current color");
    assert.equal(captureBrightness.value, "20", "stale capture completion cannot overwrite the current brightness");
    assert.equal(captureStatus.textContent, "current capture sentinel", "stale capture completion cannot overwrite current status");
    assert.equal(captureHideCount, captureHideAfterPagehide, "stale capture completion cannot hide current-page loading state");

    currentCaptureRequest.resolve({ Succeeded: true, Red: 16, Green: 32, Blue: 48, BrightnessPercent: 20 });
    await currentCaptureRequest.promise;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(captureColor.value, "#102030", "current capture updates the preview color");
    assert.equal(captureBrightness.value, "20", "current capture updates the preview brightness");
    assert.equal(captureHideCount, captureHideAfterPagehide + 1, "current capture completion hides loading state once");
}

async function testDuplicateTargetNormalizationAndGuard() {
    const harness = makeHarness();
    const { page, api, dashboard } = harness;
    const duplicateUserId = "12345678-1234-1234-1234-1234567890ab";
    const duplicateState = api.getTargetMappingDuplicateState([
        { UserId: "{" + duplicateUserId.toUpperCase() + "}", UserName: "Upper row", SyncEnabled: true },
        { userId: "123456781234123412341234567890ab", userName: "Compact row", syncEnabled: false },
        { userId: "disabled-user", userName: "Disabled one", syncEnabled: false },
        { userId: "DISABLED-USER", userName: "Disabled two", syncEnabled: false }
    ]);
    const normalizedKey = duplicateUserId.toLowerCase();
    assert.equal(duplicateState.duplicateUserIds[normalizedKey], true, "duplicate state normalizes wrapped and compact Jellyfin IDs");
    assert.equal(duplicateState.enabledDuplicateUserIds[normalizedKey], true, "one enabled row keeps a normalized duplicate group blocked");
    assert.equal(duplicateState.hasEnabledDuplicates, true, "enabled duplicate groups are detected");
    assert.equal(duplicateState.enabledDuplicateUserIds["disabled-user"], undefined, "all-disabled duplicate groups do not block broadcasts");

    const duplicateRouteValue = api.encodeCurrentLightDeviceTarget(duplicateUserId, "device-1");
    const targetOptions = [
        { value: "__all_enabled_targets__", textContent: "All enabled targets", disabled: false, title: "" },
        { value: duplicateUserId, textContent: "Upper row", disabled: false, title: "" },
        { value: duplicateRouteValue, textContent: "↳ Living room", disabled: false, title: "" }
    ];
    api.annotateTargetOptions({ options: targetOptions }, duplicateState);
    assert.equal(targetOptions[0].disabled, true, "enabled duplicates disable the all-target option");
    assert.equal(targetOptions[1].disabled, true, "enabled duplicates disable the affected mapping option");
    assert.equal(targetOptions[2].disabled, true, "enabled duplicates disable nested device routes");
    assert.match(targetOptions[1].textContent, /duplicate mapping/, "affected mapping options are annotated");

    const disabledOnlyState = api.getTargetMappingDuplicateState([
        { userId: "disabled-user", userName: "Disabled one", syncEnabled: false },
        { userId: "DISABLED-USER", userName: "Disabled two", syncEnabled: false }
    ]);
    const disabledOnlyOptions = [
        { value: "__all_enabled_targets__", textContent: "All enabled targets", disabled: false, title: "" },
        { value: "disabled-user", textContent: "Disabled one", disabled: false, title: "" }
    ];
    api.annotateTargetOptions({ options: disabledOnlyOptions }, disabledOnlyState);
    assert.equal(disabledOnlyOptions[0].disabled, false, "all-disabled duplicate groups do not disable broadcasts");
    assert.equal(disabledOnlyOptions[1].disabled, true, "all-disabled duplicate mapping rows remain unselectable");

    page._hueTargetMappingDuplicateState = duplicateState;
    const status = page.querySelector("#previewColorStatus");
    assert.equal(api.blockDuplicateTargetBroadcast(page, status), true, "enabled duplicates block broadcast actions");
    assert.match(status.textContent, /Resolve Duplicate Mappings/, "broadcast guard gives an actionable resolution path");
    assert.equal(dashboard.alerts.length, 1, "broadcast guard announces the duplicate mapping block");
}

async function testDuplicateMappingResolutionLifecycleGuards() {
    const duplicateUserId = "12345678-1234-1234-1234-1234567890ab";
    const report = {
        ReportVersion: "report-version-1",
        Mappings: [
            {
                Status: "DuplicateMapping",
                MappingId: "mapping-keeper",
                CanonicalUserId: duplicateUserId,
                PersistedUserName: "Keeper"
            },
            {
                Status: "DuplicateMapping",
                MappingId: "mapping-sibling",
                CanonicalUserId: duplicateUserId,
                PersistedUserName: "Sibling"
            }
        ]
    };

    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    const followUps = [];
    let confirmation;
    dashboard.confirm = (_message, _title, callback) => { confirmation = callback; };
    api.loadUserMappings = () => { followUps.push("mappings"); };
    api.reconcileUserMappings = () => { followUps.push("reconcile"); };
    api.refreshTargetMetadata = () => {
        followUps.push("metadata");
        return Promise.resolve();
    };

    api.renderDuplicateResolutionControls(page, report);
    const container = page.querySelector("#userMappingDuplicateResolutionControls");
    const resolveButton = container.children.find(child => child.tagName === "BUTTON");
    assert.ok(resolveButton, "duplicate resolution renders an action button");
    resolveButton.listeners.click();
    assert.equal(typeof confirmation, "function", "duplicate resolution asks for confirmation");
    confirmation(true);
    assert.equal(requests.length, 1, "confirmed duplicate resolution starts one request");
    assert.equal(requests[0].options.type, "POST", "duplicate resolution uses POST");
    assert.equal(requests[0].options.url, "HueSync/UserMappings/ResolveDuplicates", "duplicate resolution uses the API route");
    assert.deepEqual(JSON.parse(requests[0].options.data), {
        retainMappingId: "mapping-keeper",
        removeMappingIds: ["mapping-sibling"],
        expectedReportVersion: "report-version-1"
    }, "duplicate resolution sends the exact selected rows and report version");

    page.querySelector("#userMappingReconcileStatus").textContent = "unchanged after pagehide";
    api.invalidatePageLifecycle(page);
    assert.equal(requests[0].promise.aborted, true, "pagehide aborts duplicate resolution");
    assert.equal(page._huePageRequests.userMappingDuplicateResolution, undefined, "pagehide removes duplicate resolution request state");
    requests[0].resolve({ RemovedCount: 1 });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(
        page.querySelector("#userMappingReconcileStatus").textContent,
        "unchanged after pagehide",
        "invalidated duplicate resolution cannot update hidden-page status"
    );
    assert.deepEqual(followUps, [], "invalidated duplicate resolution cannot trigger stale follow-up loads");

    const confirmationHarness = makeHarness();
    const confirmationPage = confirmationHarness.page;
    const confirmationApi = confirmationHarness.api;
    confirmationHarness.dashboard.confirm = (_message, _title, callback) => { confirmation = callback; };
    confirmationApi.renderDuplicateResolutionControls(confirmationPage, report);
    const confirmationContainer = confirmationPage.querySelector("#userMappingDuplicateResolutionControls");
    const confirmationButton = confirmationContainer.children.find(child => child.tagName === "BUTTON");
    confirmationButton.listeners.click();
    confirmationApi.invalidatePageLifecycle(confirmationPage);
    confirmation(true);
    assert.equal(confirmationHarness.requests.length, 0, "a confirmation completed after pagehide cannot start duplicate resolution");
}

async function testUserMappingReconciliationLifecycleGuards() {
    const report = {
        HealthyCount: 1,
        RenamedCount: 1,
        MissingCount: 0,
        InvalidCount: 0,
        DuplicateCount: 0
    };

    const staleHarness = makeHarness();
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    let staleConfirmations = 0;
    staleHarness.dashboard.confirm = () => { staleConfirmations += 1; };
    const staleOperation = staleApi.reconcileUserMappings(stalePage);
    assert.equal(staleHarness.requests.length, 1, "reconciliation starts one tracked report request");
    assert.equal(staleHarness.requests[0].options.type, "GET", "reconciliation report uses GET");
    assert.equal(staleHarness.requests[0].options.url, "HueSync/UserMappings/Reconcile", "reconciliation report uses the reconciliation endpoint");
    stalePage.querySelector("#userMappingReconcileStatus").textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts the reconciliation report request");
    assert.equal(stalePage._huePageRequests.userMappingReconciliation, undefined, "pagehide removes reconciliation request state");
    assert.equal(stalePage.querySelector("#reconcileUserMappingsBtn").disabled, false, "pagehide restores the reconciliation button");
    staleHarness.requests[0].resolve(report);
    await staleOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(
        stalePage.querySelector("#userMappingReconcileStatus").textContent,
        "unchanged after pagehide",
        "invalidated reconciliation report cannot update hidden-page status"
    );
    assert.equal(staleConfirmations, 0, "invalidated reconciliation report cannot open a stale confirmation");

    const applyHarness = makeHarness();
    const applyPage = applyHarness.page;
    const applyApi = applyHarness.api;
    const followUps = [];
    let confirmation;
    applyHarness.dashboard.confirm = (_message, _title, callback) => { confirmation = callback; };
    applyApi.loadUserMappings = () => { followUps.push("mappings"); };
    const applyOperation = applyApi.reconcileUserMappings(applyPage);
    assert.equal(applyHarness.requests.length, 1, "apply flow starts with one report request");
    applyHarness.requests[0].resolve(report);
    await applyOperation;
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(typeof confirmation, "function", "a current renamed report asks for confirmation");
    confirmation(true);
    assert.equal(applyHarness.requests.length, 2, "confirmed reconciliation starts one apply request");
    assert.equal(applyHarness.requests[1].options.type, "POST", "reconciliation apply uses POST");
    assert.equal(applyHarness.requests[1].options.url, "HueSync/UserMappings/Reconcile", "reconciliation apply uses the reconciliation endpoint");
    assert.equal(applyPage.querySelector("#reconcileUserMappingsBtn").disabled, true, "reconciliation apply disables its button");
    applyPage.querySelector("#userMappingReconcileStatus").textContent = "unchanged after pagehide";
    applyApi.invalidatePageLifecycle(applyPage);
    assert.equal(applyHarness.requests[1].promise.aborted, true, "pagehide aborts the reconciliation apply request");
    assert.equal(applyPage.querySelector("#reconcileUserMappingsBtn").disabled, false, "pagehide restores the reconciliation button after apply");
    applyHarness.requests[1].resolve({ UpdatedCount: 1 });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(
        applyPage.querySelector("#userMappingReconcileStatus").textContent,
        "unchanged after pagehide",
        "invalidated reconciliation apply cannot update hidden-page status"
    );
    assert.deepEqual(followUps, [], "invalidated reconciliation apply cannot trigger stale mapping reloads");
}

async function testDisabledMappingCannotPreview() {
    const harness = makeHarness();
    const { page, api } = harness;
    page._huePreviewTargetMetadataReady = true;
    const syncEnabled = page.querySelector("#mappingSyncEnabled");
    const previewButton = page.querySelector("#mappingPreviewColorBtn");
    const mappingProfileControls = [
        "#mappingAudioLowGainPercentOverride",
        "#mappingAudioMidGainPercentOverride",
        "#mappingAudioHighGainPercentOverride",
        "#mappingAudioResponseSmoothingPercentOverride",
        "#mappingAudioColorPaletteOverride",
        "#mappingAudioSpatialModeOverride",
        "#mappingAudioChannelModeOverride"
    ];

    syncEnabled.checked = true;
    api.updatePreviewTargetMetadataControls(page);
    assert.equal(previewButton.disabled, false, "enabled mapping allows current-color preview");
    mappingProfileControls.forEach(selector => {
        assert.equal(page.querySelector(selector).disabled, false, `${selector} starts enabled`);
    });

    syncEnabled.checked = false;
    api.toggleMappingSyncFields(page);
    assert.equal(previewButton.disabled, true, "disabled mapping blocks current-color preview");
    mappingProfileControls.forEach(selector => {
        assert.equal(page.querySelector(selector).disabled, true, `${selector} is disabled with mapping sync off`);
    });

    syncEnabled.checked = true;
    api.toggleMappingSyncFields(page);
    api.setPreviewBusy(page, true);
    api.setPreviewBusy(page, false);
    assert.equal(previewButton.disabled, false, "ending a preview restores enabled mapping preview state");
    mappingProfileControls.forEach(selector => {
        assert.equal(page.querySelector(selector).disabled, false, `${selector} is restored when mapping sync is on`);
    });
}

async function testSavedSceneSingleMappingUsesSelectedTargetPayload() {
    const harness = makeHarness();
    const { page, api, requests } = harness;
    page._huePreviewTargetMetadataReady = true;

    const targetSelect = page.querySelector("#previewSavedPresetTarget");
    targetSelect.options = [{ value: "user-one", selected: true }];
    targetSelect.selectedOptions = targetSelect.options;

    const selection = api.getSavedPresetTargetSelection(page);
    assert.deepEqual(Array.from(selection.targetUserIds), ["user-one"], "one mapping remains selected-target mode");
    assert.equal(selection.targetUserId, "", "one selected mapping does not populate the legacy targetUserId field");

    const singleOperation = api.fetchSavedColorPreview("scene-one", selection);
    assert.equal(requests.length, 1, "single saved-scene preview starts one request");
    assert.deepEqual(
        JSON.parse(requests[0].options.data),
        {
            targetUserId: "",
            targetAllEnabledMappings: false,
            targetUserIds: ["user-one"],
            targetRoutes: [],
            includeDefaultTarget: false
        },
        "single saved-scene preview sends only the selected-target representation"
    );
    requests[0].resolve({ succeeded: true, message: "single preview" });
    await singleOperation;

    const bulkSelect = page.querySelector("#previewPresetBulkSelect");
    bulkSelect.options = [{ value: "scene-one", selected: true }];
    let confirm;
    harness.dashboard.confirm = (_message, _title, callback) => { confirm = callback; };
    api.previewColorPresetsBulk(page, false);
    assert.equal(typeof confirm, "function", "bulk saved-scene preview asks for confirmation");
    confirm(true);
    assert.equal(requests.length, 2, "bulk saved-scene preview starts one request");
    assert.deepEqual(
        JSON.parse(requests[1].options.data),
        {
            presetNames: ["scene-one"],
            targetUserId: "",
            targetUserIds: ["user-one"],
            targetRoutes: [],
            includeDefaultTarget: false,
            targetAllEnabledMappings: false
        },
        "bulk saved-scene preview sends only the selected-target representation"
    );
    requests[1].resolve({ succeeded: true, message: "bulk preview", previews: [] });
    await new Promise(resolve => setImmediate(resolve));
}

async function testBridgeCertificatePinRenderingAndForgetLifecycle() {
    const loadHarness = makeHarness();
    const loadPage = loadHarness.page;
    const loadApi = loadHarness.api;
    loadApi.loadEntertainmentAreas = () => {};
    loadApi.loadColorPresets = () => {};
    loadApi.loadScenePlaylists = () => {};
    loadApi.loadSceneSchedules = () => {};
    const load = loadApi.loadConfiguration(loadPage);
    assert.equal(loadHarness.requests.length, 1, "configuration load starts one tracked request for certificate pins");
    loadHarness.requests[0].resolve({
        HueBridgeIp: "192.168.1.100",
        HueBridgeCertificatePins: {
            "192.168.1.100": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        }
    });
    await load;
    assert.equal(
        loadPage.querySelector("#bridgeCertificatePinsList").children.length,
        1,
        "configuration load renders the certificate pins returned by the API"
    );

    const harness = makeHarness();
    const { page, api, requests, dashboard } = harness;
    page._hueBridgeCertificatePins = {
        "192.168.1.101": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "192.168.1.100": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    };
    api.renderBridgeCertificatePins(page);

    const list = page.querySelector("#bridgeCertificatePinsList");
    assert.equal(list.children.length, 2, "certificate pin rendering shows every stored host");
    assert.equal(
        list.children[0].children[0].textContent,
        "192.168.1.100 — aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "certificate pin rendering exposes only host and fingerprint metadata"
    );
    assert.equal(list.children[0].children[1].textContent, "Forget", "certificate pin rendering adds a forget control");
    assert.equal(
        list.children[0].children[1].attributes["aria-label"],
        "Forget trusted certificate for 192.168.1.100",
        "certificate pin forget controls identify their host accessibly"
    );

    let confirm;
    dashboard.confirm = (_message, _title, callback) => { confirm = callback; };
    const operation = api.forgetBridgeCertificatePin(page, "192.168.1.100");
    assert.equal(requests.length, 0, "forget requires explicit confirmation before mutating configuration");
    assert.equal(list.children[0].children[1].disabled, true, "pending forget disables pin controls");
    confirm(true);
    assert.equal(requests.length, 1, "confirmed forget starts one tracked request");
    assert.equal(requests[0].options.type, "DELETE", "forget uses DELETE");
    assert.equal(
        requests[0].options.url,
        "HueSync/BridgeCertificate/Trust?ipAddress=192.168.1.100",
        "forget scopes the request to the selected host"
    );
    assert.equal(requests[0].options.dataType, "text", "forget accepts the endpoint's empty response safely");
    requests[0].resolve("");
    await operation;
    assert.equal(Object.keys(page._hueBridgeCertificatePins).length, 1, "successful forget removes only the selected cached pin");
    assert.equal(list.children.length, 1, "successful forget re-renders remaining pins");
    assert.equal(page.querySelector("#bridgeStatus").style.background, "#f0ad4e", "successful forget warns that re-trust is required");
    assert.equal(page._hueBridgeCertificatePinRemoving, false, "successful forget clears its busy state");

    const staleHarness = makeHarness();
    const stalePage = staleHarness.page;
    const staleApi = staleHarness.api;
    stalePage._hueBridgeCertificatePins = {
        "192.168.1.100": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    };
    staleApi.renderBridgeCertificatePins(stalePage);
    let staleConfirm;
    staleHarness.dashboard.confirm = (_message, _title, callback) => { staleConfirm = callback; };
    const staleOperation = staleApi.forgetBridgeCertificatePin(stalePage, "192.168.1.100");
    staleConfirm(true);
    assert.equal(staleHarness.requests.length, 1, "stale lifecycle test starts a tracked forget request");
    stalePage.querySelector("#bridgeStatus").textContent = "unchanged after pagehide";
    staleApi.invalidatePageLifecycle(stalePage);
    assert.equal(staleHarness.requests[0].promise.aborted, true, "pagehide aborts an in-flight pin forget request");
    staleHarness.requests[0].resolve("");
    await staleOperation;
    assert.equal(
        stalePage._hueBridgeCertificatePins["192.168.1.100"],
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "invalidated forget cannot mutate the hidden page's pin cache"
    );
    assert.equal(
        stalePage.querySelector("#bridgeStatus").textContent,
        "unchanged after pagehide",
        "invalidated forget cannot write a stale status"
    );
}

for (const testCase of exportCases) {
    await testSuccessfulExport(testCase);
    await testStaleQuerySuppressesExport(testCase);
    await testInvalidatedPageSuppressesExport(testCase);
    await testCurrentFailure(testCase);
    await testDuplicateClickIsBounded(testCase);
}

await testEditMappingLifecycleGuards();
await testUserMappingSaveLifecycleGuards();
await testUserMappingDeleteLifecycleGuards();
await testUserMappingCleanupLifecycleGuards();
await testUserMappingBulkDeleteLifecycleGuards();
await testUserMappingBulkEnabledLifecycleGuards();
await testConfigurationImportValidationLifecycleGuards();
await testConfigurationImportFileLifecycleGuards();
await testMappingDeviceRouteCredentialScope();
await testStoredDeviceRouteCredentialFlags();
await testCredentialPreflightPagehideGuard();
await testBridgeCertificateTrustPromptPagehideGuard();
await testBridgeCertificateTrustRequestLifecycle();
await testCredentialPreflightCancelGuard();
await testCredentialPreflightTargetMutationGuard();
await testCredentialLifecyclePreflightPagehideGuard();
await testRegistrationLifecycleGuards();
await testMappingDeviceRouteChannelIsolation();
await testMappingDeviceDiscoveryLifecycleGuards();
await testBridgeDiscoveryLifecycleGuards();
await testConfigurationImportSubmitLifecycleGuards();
await testConfigurationSaveSuppressesStaleConfigurationLoad();
await testConfigurationSaveInvalidationSuppressesCallbacks();
await testConfigurationSaveDuplicateSubmitIsBounded();
await testColorPresetSaveLifecycleGuards();
await testColorPresetDuplicateLifecycleGuards();
await testColorPresetDeleteLifecycleGuards();
await testColorPresetBulkDeleteLifecycleGuards();
await testColorPresetBulkDuplicateLifecycleGuards();
await testColorPresetBulkErrorDetailsAndRetry();
await testColorPresetRenameLifecycleGuards();
await testColorPresetDependenciesLifecycleGuards();
await testScenePlaylistSaveLifecycleGuards();
await testScenePlaylistDeleteLifecycleGuards();
await testScenePlaylistIndividualMutationLifecycleGuards();
await testScenePlaylistDependenciesLifecycleGuards();
await testScenePlaylistMutationLockGuards();
await testScenePlaylistMutationSelectionGuards();
await testScenePlaylistBulkLifecycleGuards();
await testScenePlaylistSharedMutationLockArbitration();
await testHistoryClearLifecycleGuards();
await testSceneScheduleSaveLifecycleGuards();
await testSceneScheduleBulkMutationLifecycleGuards();
await testSceneScheduleDirectMutationLifecycleGuards();
await testSceneScheduleDeleteLifecycleGuards();
await testSceneScheduleRunCancellationUsesActiveId();
await testSceneScheduleBulkRunLifecycleGuards();
await testSceneScheduleReloadLeaseGuardsActions();
await testEntertainmentAreaSelectionHandlesUnsafeIds();
await testPreviewLifecyclePagehideGuards();
await testDuplicateTargetNormalizationAndGuard();
await testDuplicateMappingResolutionLifecycleGuards();
await testUserMappingReconciliationLifecycleGuards();
await testRuntimeStopLifecycleGuards();
await testDisabledMappingCannotPreview();
await testSavedSceneSingleMappingUsesSelectedTargetPayload();
await testBridgeCertificatePinRenderingAndForgetLifecycle();

console.log(`Configuration lifecycle contracts passed (${exportCases.length} exports plus mapping-edit/save/delete/cleanup/bulk-delete/bulk-enabled lifecycle, scoped route credentials/channel isolation/device-discovery pagehide and retry lifecycle, bridge-discovery pagehide/retry/target-mutation lifecycle, certificate preflight/trust-prompt/cancel/pagehide/target-mutation, certificate pin rendering/forget lifecycle, registration lifecycle, import file/validation/submit, configuration and color-preset save/duplicate/delete/bulk-delete/bulk-duplicate/bulk-error-retry/rename/scene save/delete/duplicate/rename/dependencies/bulk-delete/bulk-duplicate/shared-mutation-lock-arbitration/history-clear/schedule-delete/bulk-mutation stale-confirmation/pagehide/stale-completion/current-success, scheduled-cue run/cancel identity, unsafe area-ID selection, preview/capture pagehide and stale-completion guards, duplicate-target, duplicate-resolution, user-mapping reconciliation, runtime-stop pagehide, disabled-mapping preview, and single-mapping saved-scene preview payload paths)`);
