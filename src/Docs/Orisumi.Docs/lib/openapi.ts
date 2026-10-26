import type { Document } from 'fumadocs-openapi';
import { createOpenAPI } from 'fumadocs-openapi/server';
import schema from '@/content/docs/api/openapi.json';

export const openapiSchema = schema;

export const openapi = createOpenAPI({
  input: {
    './content/docs/api/openapi.json': schema as Document,
  },
});
