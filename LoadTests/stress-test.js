import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';
import { SharedArray } from 'k6/data';
import ws from 'k6/ws';

// Custom metrics
const errorRate = new Rate('errors');
const pushDuration = new Trend('push_duration');
const pullDuration = new Trend('pull_duration');
const todosCreated = new Counter('todos_created');

// Load test configuration
export const options = {
    stages: [
        { duration: '30s', target: 50 },   // Ramp up to 50 users
        { duration: '1m', target: 100 },   // Ramp up to 100 users
        { duration: '2m', target: 200 },   // Ramp up to 200 users
        { duration: '2m', target: 500 },   // Ramp up to 500 users
        { duration: '2m', target: 1000 },  // Ramp up to 1000 users
        { duration: '3m', target: 1000 },  // Stay at 1000 users
        { duration: '1m', target: 0 },     // Ramp down to 0 users
    ],
    thresholds: {
        'http_req_duration': ['p(95)<2000', 'p(99)<5000'], // 95% of requests must complete below 2s, 99% below 5s
        'http_req_failed': ['rate<0.05'],  // Error rate must be less than 5%
        'errors': ['rate<0.1'],
        'push_duration': ['p(95)<3000'],
        'pull_duration': ['p(95)<1000'],
    },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:3000';

// Helper function to generate unique ID
function generateId() {
    return `${Date.now()}-${Math.random().toString(36).substr(2, 9)}-${__VU}-${__ITER}`;
}

// Helper function to create todo event
function createTodoEvent(todoId, title, priority = 'MEDIUM') {
    return {
        eventId: generateId(),
        type: 'TODO_CREATED',
        todoId: todoId,
        payload: {
            title: title,
            priority: priority,
            dayKey: '2026-03-14'
        },
        createdAt: Date.now(),
        synced: 0
    };
}

// Helper function to toggle todo event
function toggleTodoEvent(todoId) {
    return {
        eventId: generateId(),
        type: 'TODO_TOGGLED',
        todoId: todoId,
        createdAt: Date.now(),
        synced: 0
    };
}

// Helper function to rename todo event
function renameTodoEvent(todoId, newTitle, priority = 'LOW') {
    return {
        eventId: generateId(),
        type: 'TODO_RENAMED',
        todoId: todoId,
        payload: {
            title: newTitle,
            priority: priority
        },
        createdAt: Date.now(),
        synced: 0
    };
}

// Helper function to delete todo event
function deleteTodoEvent(todoId) {
    return {
        eventId: generateId(),
        type: 'TODO_DELETED',
        todoId: todoId,
        createdAt: Date.now(),
        synced: 0
    };
}

// Main test scenario
export default function () {
    const userId = `user-${__VU}`;
    const todoId = generateId();

    // Scenario 1: Create a todo
    {
        const createEvent = createTodoEvent(todoId, `Task from ${userId} - iteration ${__ITER}`);
        const pushPayload = JSON.stringify({ events: [createEvent] });

        const pushStart = Date.now();
        const pushRes = http.post(`${BASE_URL}/api/sync/push`, pushPayload, {
            headers: { 'Content-Type': 'application/json' },
            tags: { name: 'push_create' }
        });

        const pushSuccess = check(pushRes, {
            'push status is 200': (r) => r.status === 200,
            'push has acceptedEventIds': (r) => {
                try {
                    const body = JSON.parse(r.body);
                    return body.acceptedEventIds && body.acceptedEventIds.length > 0;
                } catch (e) {
                    return false;
                }
            }
        });

        pushDuration.add(Date.now() - pushStart);
        errorRate.add(!pushSuccess);
        if (pushSuccess) {
            todosCreated.add(1);
        }
    }

    sleep(0.5);

    // Scenario 2: Pull todos
    {
        const pullStart = Date.now();
        const pullRes = http.get(`${BASE_URL}/api/sync/pull?since=0`, {
            tags: { name: 'pull_all' }
        });

        const pullSuccess = check(pullRes, {
            'pull status is 200': (r) => r.status === 200,
            'pull has todos': (r) => {
                try {
                    const body = JSON.parse(r.body);
                    return body.todos !== undefined && body.serverTime !== undefined;
                } catch (e) {
                    return false;
                }
            }
        });

        pullDuration.add(Date.now() - pullStart);
        errorRate.add(!pullSuccess);
    }

    sleep(0.5);

    // Scenario 3: Toggle the todo
    {
        const toggleEvent = toggleTodoEvent(todoId);
        const pushPayload = JSON.stringify({ events: [toggleEvent] });

        const pushRes = http.post(`${BASE_URL}/api/sync/push`, pushPayload, {
            headers: { 'Content-Type': 'application/json' },
            tags: { name: 'push_toggle' }
        });

        check(pushRes, {
            'toggle status is 200': (r) => r.status === 200
        });
    }

    sleep(0.5);

    // Scenario 4: Rename the todo
    {
        const renameEvent = renameTodoEvent(todoId, `Updated task ${userId}`);
        const pushPayload = JSON.stringify({ events: [renameEvent] });

        const pushRes = http.post(`${BASE_URL}/api/sync/push`, pushPayload, {
            headers: { 'Content-Type': 'application/json' },
            tags: { name: 'push_rename' }
        });

        check(pushRes, {
            'rename status is 200': (r) => r.status === 200
        });
    }

    sleep(0.5);

    // Scenario 5: Pull with v2 API (pagination)
    {
        const pullV2Res = http.get(`${BASE_URL}/api/sync/v2/pull?sinceChangeId=0&limit=50`, {
            tags: { name: 'pull_v2' }
        });

        check(pullV2Res, {
            'pull_v2 status is 200': (r) => r.status === 200,
            'pull_v2 has changes': (r) => {
                try {
                    const body = JSON.parse(r.body);
                    return body.changes !== undefined;
                } catch (e) {
                    return false;
                }
            }
        });
    }

    sleep(1);

    // Scenario 6: Delete the todo (20% chance)
    if (Math.random() < 0.2) {
        const deleteEvent = deleteTodoEvent(todoId);
        const pushPayload = JSON.stringify({ events: [deleteEvent] });

        const pushRes = http.post(`${BASE_URL}/api/sync/push`, pushPayload, {
            headers: { 'Content-Type': 'application/json' },
            tags: { name: 'push_delete' }
        });

        check(pushRes, {
            'delete status is 200': (r) => r.status === 200
        });
    }

    sleep(1);
}

// Health check test
export function healthCheck() {
    const res = http.get(`${BASE_URL}/`);
    check(res, {
        'health check status is 200': (r) => r.status === 200,
        'health check has service name': (r) => r.body.includes('TodoSync.Api')
    });
}
