import React, { useState, useEffect, useRef } from 'react';
import { v4 as uuidv4 } from 'uuid';


import * as lib from '../../../lib/searches';
import { createSearchLogHubConnection } from '../../../lib/hubFactory';

import SearchListRow from './SearchListRow';
import ErrorSegment from '../../Shared/ErrorSegment';
import Switch from '../../Shared/Switch';

import {
  Card,
  Table,
  Icon,
  Loader
} from 'semantic-ui-react';
import SearchIcon from '../SearchIcon';

const SearchList = () => {
  const [{ connected, connecting, connectError} , setConnected] = useState({ connected: false, connecting: true, connectError: false });
  const [{ creating, createError }, setCreating] = useState({ creating: false, createError: false });
  const [error, setError] = useState(undefined);
  const [searches, setSearches] = useState({});
  const inputRef = useRef();

  const onConnecting = () => setConnected({ connected: false, connecting: true, connectError: false })
  const onConnected = () => setConnected({ connected: true, connecting: false, connectError: false });
  const onConnectionError = (error) => setConnected({ connected: false, connecting: false, connectError: error })

  const onUpdate = (update) => {
    onConnected();
    setSearches(update);
  }

  useEffect(() => {
    onConnecting();
    
    const searchHub = createSearchLogHubConnection();

    searchHub.on('list', searches => {
      onUpdate(searches.reduce((acc, search) => {
        acc[search.id] = search;
        return acc;
      }, {}));
      onConnected();
    })

    searchHub.on('update', search => {
      onUpdate(old => ({ ...old, [search.id]: search }));
    });

    searchHub.on('delete', search => {
      onUpdate(old => {
        delete old[search.id];
        return { ...old }
      });
    });

    searchHub.onreconnecting((error) => onConnectionError(error?.message ?? 'Disconnected'));
    searchHub.onreconnected(() => onConnected());
    searchHub.onclose((error) => onConnectionError(error?.message ?? 'Disconnected'));

    const connect = async () => {
      try {
        onConnecting();
        await searchHub.start();
      } catch (error) {
        onConnectionError(error?.message ?? 'Failed to connect')
      }
    }

    connect();

    return () => {
      searchHub.stop();
    }
  }, []);

  const create = async () => {
    const searchText = inputRef.current.inputRef.current.value;
    const id = uuidv4();

    try {
      setCreating({ creating: true, createError: false })
      lib.create({ id, searchText })
      setCreating({ creating: false, createError: false })
      inputRef.current.inputRef.current.value = '';
    } catch (error) {
      setCreating({ creating: false, createError: error.message })
    }
  }

  const get = async () => {
    
  }

  const remove = async (search) => {
    console.log('remove', searches)
    try {
      await lib.remove({ id: search.id })
      setSearches(old => {
        delete old[search.id];
        return { ...old }
      });
    } catch (err) {
      console.error(err)
      // noop
    }
  }

  const stop = async (search) => {
    await lib.stop({ id: search.id })
  }

  const cancelAndDeleteAll = () => {
    // todo
  }

  const TableContents = () => (
    <>
      {Object.values(searches)
      .sort((a, b) => (new Date(b.startedAt) - new Date(a.startedAt)))
      .map((search, index) => <SearchListRow
        search={search}
        key={index}
        onRemove={remove}
        onStop={stop}
      />)}
    </>
  );

  return (
    <>
      <Card className='search-card' raised>
        <Card.Content>
          <Card.Header>
            <Icon name='search'/>
            Searches
            <Icon.Group className='close-button' style={{ marginLeft: 10 }}>
              <Icon 
                name='trash alternate' 
                color='red' 
                link={connected}
                disabled={!connected}
                onClick={() => cancelAndDeleteAll()}
              />
              <Icon corner name='asterisk' disabled={!connected}/>
            </Icon.Group>
            <Icon.Group className='close-button' >
              <Icon 
                name='stop circle' 
                color='black' 
                link={connected}
                disabled={!connected}
                onClick={() => cancelAndDeleteAll()}
              />
              <Icon corner name='asterisk' disabled={!connected}/>
            </Icon.Group>
          </Card.Header>
          <Card.Description>
            <Switch
              connecting={connecting && <Loader active inline='centered' size='small'/>}
              error={error && <ErrorSegment caption={error}/>}
            >
              <Table selectable>
                <Table.Header>
                  <Table.Row>
                    <Table.HeaderCell className="search-list-icon"></Table.HeaderCell>
                    <Table.HeaderCell className="search-list-phrase">Search Phrase</Table.HeaderCell>
                    <Table.HeaderCell className="search-list-files">Files</Table.HeaderCell>
                    <Table.HeaderCell className="search-list-locked">Locked</Table.HeaderCell>
                    <Table.HeaderCell className="search-list-responses">Responses</Table.HeaderCell>
                    <Table.HeaderCell className="search-list-started">Started</Table.HeaderCell>
                    <Table.HeaderCell className="search-list-action"></Table.HeaderCell>
                  </Table.Row>
                </Table.Header>
                <Table.Body>
                  <TableContents/>
                </Table.Body>
              </Table>
            </Switch>
          </Card.Description>
        </Card.Content>
      </Card>
    </>
  )
};

export default SearchList;