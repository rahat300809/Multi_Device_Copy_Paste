# Ultra-lightweight Python Alpine image (~40MB)
FROM python:3.11-alpine

WORKDIR /app

# Install minimal dependencies without caching to keep image size tiny
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy server code
COPY server.py .

# Environment variables
ENV PORT=8765
ENV PYTHONUNBUFFERED=1

EXPOSE 8765

HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD wget --no-verbose --tries=1 --spider http://localhost:8765/health || exit 1

CMD ["python", "server.py"]
