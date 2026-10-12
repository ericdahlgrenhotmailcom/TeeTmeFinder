import React, { useEffect, useState, useRef } from 'react';
import '../index.css';

const API_BASE = 'http://localhost:5000';

export default function GolfMonitor() {
  const [startDate, setStartDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [endDate, setEndDate] = useState(() => {
    const d = new Date();
    d.setDate(d.getDate() + 7);
    return d.toISOString().slice(0, 10);
  });
  const [minTime, setMinTime] = useState('06:00');
  const [maxTime, setMaxTime] = useState('22:00');
  const [requiredSlots, setRequiredSlots] = useState(3);
  const [webhookUrl, setWebhookUrl] = useState('');

  const [isActive, setIsActive] = useState(false);
  const [logs, setLogs] = useState([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);

  const logsRef = useRef(null);

  // periodic status refresh
  useEffect(() => {
    let mounted = true;
    const poll = async () => {
      try {
        const res = await fetch(`${API_BASE}/api/monitor/status`);
        if (!res.ok) throw new Error(`Status ${res.status}`);
        const data = await res.json();
        if (!mounted) return;
        setIsActive(Boolean(data.isActive));
        setLogs(Array.isArray(data.logs) ? data.logs : []);
        // do not overwrite settings when monitor is active
        if (!data.isActive) {
          if (data.startDate) setStartDate(data.startDate.slice(0, 10));
          if (data.endDate) setEndDate(data.endDate.slice(0, 10));
          if (data.minTime) setMinTime(formatTimeSpanToInput(data.minTime));
          if (data.maxTime) setMaxTime(formatTimeSpanToInput(data.maxTime));
          if (data.requiredSlots) setRequiredSlots(data.requiredSlots);
          if (data.webhookUrl) setWebhookUrl(data.webhookUrl);
        }
      } catch (e) {
        // ignore transient errors
      }
    };

    poll();
    const id = setInterval(poll, 3000);
    return () => { mounted = false; clearInterval(id); };
  }, []);

  useEffect(() => {
    // auto-scroll logs
    if (logsRef.current) {
      logsRef.current.scrollTop = logsRef.current.scrollHeight;
    }
  }, [logs]);

  function formatTimeSpanToInput(ts) {
    // backend may return TimeSpan like "06:00:00" or "00:06:00"; normalize to HH:mm
    if (!ts) return '06:00';
    const parts = ts.split(':');
    if (parts.length >= 2) return `${parts[0].padStart(2, '0')}:${parts[1].padStart(2, '0')}`;
    return ts;
  }

  async function startMonitor() {
    setLoading(true); setError(null);
    try {
      const payload = {
        StartDate: new Date(startDate).toISOString(),
        EndDate: new Date(endDate).toISOString(),
        MinTime: minTime + ':00',
        MaxTime: maxTime + ':00',
        RequiredSlots: Number(requiredSlots),
        WebhookUrl: webhookUrl || null
      };

      const res = await fetch(`${API_BASE}/api/monitor/start`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });

      if (!res.ok) throw new Error(`Start failed: ${res.status}`);
      setIsActive(true);
    } catch (e) {
      setError(e.message);
    } finally {
      setLoading(false);
    }
  }

  async function stopMonitor() {
    setLoading(true); setError(null);
    try {
      const res = await fetch(`${API_BASE}/api/monitor/stop`, { method: 'POST' });
      if (!res.ok) throw new Error(`Stop failed: ${res.status}`);
      setIsActive(false);
    } catch (e) {
      setError(e.message);
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="max-w-xl mx-auto p-4 sm:p-6 lg:p-8">
      <div className="bg-white shadow-md rounded-lg p-4 sm:p-6">
        <h2 className="text-xl font-semibold mb-3">Golf Monitor</h2>
        <p className="text-sm text-gray-500 mb-4">Configure monitoring parameters and receive Discord webhook alerts.</p>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <label className="flex flex-col">
            <span className="text-xs font-medium text-gray-600">Start Date</span>
            <input type="date" value={startDate} onChange={e => setStartDate(e.target.value)} disabled={isActive}
              className="mt-1 px-3 py-2 border rounded-md focus:outline-none focus:ring" />
          </label>

          <label className="flex flex-col">
            <span className="text-xs font-medium text-gray-600">End Date</span>
            <input type="date" value={endDate} onChange={e => setEndDate(e.target.value)} disabled={isActive}
              className="mt-1 px-3 py-2 border rounded-md focus:outline-none focus:ring" />
          </label>

          <label className="flex flex-col">
            <span className="text-xs font-medium text-gray-600">Min Time</span>
            <input type="time" value={minTime} onChange={e => setMinTime(e.target.value)} disabled={isActive}
              className="mt-1 px-3 py-2 border rounded-md focus:outline-none focus:ring" />
          </label>

          <label className="flex flex-col">
            <span className="text-xs font-medium text-gray-600">Max Time</span>
            <input type="time" value={maxTime} onChange={e => setMaxTime(e.target.value)} disabled={isActive}
              className="mt-1 px-3 py-2 border rounded-md focus:outline-none focus:ring" />
          </label>

          <label className="flex flex-col">
            <span className="text-xs font-medium text-gray-600">Required Slots</span>
            <select value={requiredSlots} onChange={e => setRequiredSlots(e.target.value)} disabled={isActive}
              className="mt-1 px-3 py-2 border rounded-md focus:outline-none focus:ring">
              {[1,2,3,4].map(n => <option key={n} value={n}>{n}</option>)}
            </select>
          </label>

          <label className="flex flex-col sm:col-span-2">
            <span className="text-xs font-medium text-gray-600">Discord Webhook URL</span>
            <input type="text" value={webhookUrl} onChange={e => setWebhookUrl(e.target.value)} disabled={isActive}
              placeholder="https://discord.com/api/webhooks/..." className="mt-1 px-3 py-2 border rounded-md focus:outline-none focus:ring" />
          </label>
        </div>

        <div className="flex items-center gap-3 mt-4">
          <button onClick={startMonitor} disabled={isActive || loading}
            className={`px-4 py-2 rounded-md font-medium text-white ${isActive ? 'bg-green-300 cursor-not-allowed' : 'bg-green-600 hover:bg-green-700'}`}>
            {loading && !isActive ? 'Starting...' : 'Start Monitoring'}
          </button>

          <button onClick={stopMonitor} disabled={!isActive || loading}
            className={`px-4 py-2 rounded-md font-medium text-white ${!isActive ? 'bg-red-300 cursor-not-allowed' : 'bg-red-600 hover:bg-red-700'}`}>
            {loading && isActive ? 'Stopping...' : 'Stop'}
          </button>

          <div className="ml-auto text-sm text-gray-600">Status: <span className={`font-semibold ${isActive ? 'text-green-600' : 'text-gray-500'}`}>{isActive ? 'Active' : 'Idle'}</span></div>
        </div>

        {error && <div className="mt-3 text-sm text-red-600">{error}</div>}

        <div className="mt-4">
          <h3 className="text-sm font-medium text-gray-700 mb-2">Activity Log</h3>
          <div ref={logsRef} className="h-48 overflow-y-auto p-3 bg-gray-50 border rounded-md text-xs text-gray-700">
            {logs.length === 0 && <div className="text-gray-400">No activity yet.</div>}
            {logs.map((l, i) => <div key={i} className="mb-1 whitespace-pre-wrap">{l}</div>)}
          </div>
        </div>
      </div>

      <div className="text-center text-xs text-gray-500 mt-3">Powered by TeeTmeFinder · backend: http://localhost:5000</div>
    </div>
  );
}
