// Credentials for an existing test API are supplied at runtime, never committed.
function requiredPassword(name = 'ADMIN_PASSWORD') {
  const value = process.env[name];
  if (!value) throw new Error(`Set ${name} to the test API password before running this script.`);
  return value;
}
module.exports = { requiredPassword };
