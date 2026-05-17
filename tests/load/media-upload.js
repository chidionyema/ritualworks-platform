import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

const uploadSuccess = new Rate('upload_success_rate');
const uploadLatency = new Trend('upload_initiate_latency', true);

export const options = {
  scenarios: {
    sustained: {
      executor: 'constant-arrival-rate',
      rate: 20,              // 20 uploads/second
      timeUnit: '1s',
      duration: '5m',
      preAllocatedVUs: 50,
      maxVUs: 100,
    },
  },
  thresholds: {
    'upload_success_rate': ['rate>0.99'],
    'upload_initiate_latency': ['p(99)<2000'],  // <2s to get presigned URL
    'http_req_failed': ['rate<0.01'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:5000';

export default function () {
  const token = __ENV.AUTH_TOKEN || 'test-token';
  const headers = {
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${token}`,
  };

  // Initiate upload — measures presigned URL generation latency
  const start = Date.now();
  const res = http.post(`${BASE_URL}/api/media/initiate`, JSON.stringify({
    fileName: `loadtest-${__VU}-${__ITER}.jpg`,
    hash: `${__VU}${__ITER}`.padStart(64, 'a'),
    size: 1024 * 1024 * 5, // 5MB
    mimeType: 'image/jpeg',
  }), { headers });

  const success = check(res, {
    'initiate 200': (r) => r.status === 200,
    'has upload url': (r) => {
      if (r.status !== 200) return false;
      const body = JSON.parse(r.body);
      return body.uploadUrl || body.alreadyExists;
    },
  });

  uploadLatency.add(Date.now() - start);
  uploadSuccess.add(success);
  sleep(0.1);
}
