'use strict';

const crypto = require('crypto');

module.exports = function createWorkspaceModule(parent) {
    const sessions = new Map();

    function now() { return new Date().toISOString(); }
    function makeId() { return crypto.randomBytes(16).toString('hex'); }

    function createSession(nodeId, userId) {
        const session = {
            id: makeId(),
            nodeId,
            userId: userId || null,
            state: 'requested',
            createdAt: now(),
            updatedAt: now(),
            pid: null,
            windowsSessionId: null,
            user: null,
            desktop: null,
            version: null,
            lastHeartbeat: null,
            error: null
        };
        sessions.set(session.id, session);
        return session;
    }

    function updateSession(id, patch) {
        const session = sessions.get(id);
        if (!session) return null;
        Object.assign(session, patch, { updatedAt: now() });
        return session;
    }

    function registerRoutes(app) {
        if (!app || typeof app.post !== 'function') return false;

        app.post('/workspace/api/session/start', (req, res) => {
            const nodeId = req.body && req.body.nodeId;
            if (!nodeId || typeof nodeId !== 'string') {
                return res.status(400).json({ error: 'nodeId is required' });
            }

            const session = createSession(nodeId, req.user && req.user._id);
            // Transport through MeshAgent is the next integration boundary.
            // The UI receives a real server-side session immediately and can poll status.
            return res.status(202).json(session);
        });

        app.get('/workspace/api/session/:id', (req, res) => {
            const session = sessions.get(req.params.id);
            if (!session) return res.status(404).json({ error: 'session not found' });
            return res.json(session);
        });

        app.post('/workspace/api/session/:id/heartbeat', (req, res) => {
            const body = req.body || {};
            const session = updateSession(req.params.id, {
                state: 'running',
                pid: body.pid || null,
                windowsSessionId: body.sessionId ?? null,
                user: body.user || null,
                desktop: body.desktop || 'Default',
                version: body.version || null,
                lastHeartbeat: now(),
                error: null
            });
            if (!session) return res.status(404).json({ error: 'session not found' });
            return res.json({ ok: true, session });
        });

        app.post('/workspace/api/session/:id/stop', (req, res) => {
            const session = updateSession(req.params.id, { state: 'stopped' });
            if (!session) return res.status(404).json({ error: 'session not found' });
            return res.json(session);
        });

        return true;
    }

    return { sessions, createSession, updateSession, registerRoutes };
};
